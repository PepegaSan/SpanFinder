using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Span.Helpers
{
    /// <summary>
    /// Details 뷰 컬럼 하나의 정의. <see cref="DetailsColumns.All"/>의 순서가 곧 화면 순서이고,
    /// 항목 템플릿에서 셀의 Grid.Column은 (인덱스 + 2)다 (0 = 아이콘, 1 = 이름).
    /// </summary>
    public sealed record DetailsColumnInfo(
        string Id,
        string LocKey,
        double DefaultWidth,
        double MinWidth,
        bool DefaultVisible,
        bool UserToggleable,
        bool IsShellProperty);

    /// <summary>
    /// Details 뷰에서 쓸 수 있는 컬럼 목록 (Windows 탐색기의 "열 선택"에 해당).
    /// Name은 항상 표시되므로 목록에 없다.
    /// </summary>
    public static class DetailsColumns
    {
        public const string Location = "Location";
        public const string DateModified = "DateModified";
        public const string Type = "Type";
        public const string Size = "Size";
        public const string DateCreated = "DateCreated";
        public const string Attributes = "Attributes";
        public const string Dimensions = "Dimensions";
        public const string Duration = "Duration";
        public const string Authors = "Authors";
        public const string Title = "Title";
        public const string Git = "Git";
        public const string Tag = "Tag";

        /// <summary>항목 템플릿에서 첫 컬럼 셀의 Grid.Column.</summary>
        public const int FirstCellGridColumn = 2;

        /// <summary>
        /// 화면 순서. Location은 재귀 검색 결과에서만, Git은 Git 저장소에서만 자동으로 보인다.
        /// 순서를 바꾸면 DetailsModeView.xaml의 셀 Grid.Column도 같이 바꿔야 한다.
        /// </summary>
        public static readonly DetailsColumnInfo[] All =
        {
            new(Location,     "Location",          180, 100, false, false, false),
            new(DateModified, "DateModified",      200,  80, true,  true,  false),
            new(Type,         "Type",              150,  50, true,  true,  false),
            new(Size,         "Size",              100,  50, true,  true,  false),
            new(DateCreated,  "Column_DateCreated", 160, 80, false, true,  false),
            new(Attributes,   "Column_Attributes",  80,  40, false, true,  false),
            new(Dimensions,   "Column_Dimensions", 110,  50, false, true,  true),
            new(Duration,     "Column_Duration",    90,  50, false, true,  true),
            new(Authors,      "Column_Authors",    150,  60, false, true,  true),
            new(Title,        "Column_Title",      180,  60, false, true,  true),
            new(Git,          "ColumnGit",          50,  40, false, true,  false),
            new(Tag,          "Column_Tag",        110,  50, false, true,  false),
        };

        public static int IndexOf(string id)
        {
            for (int i = 0; i < All.Length; i++)
                if (All[i].Id == id) return i;
            return -1;
        }

        public static DetailsColumnInfo? Find(string id)
        {
            int i = IndexOf(id);
            return i >= 0 ? All[i] : null;
        }

        public static bool IsShellColumn(string id) => Find(id)?.IsShellProperty == true;

        /// <summary>
        /// 탐색기 "특성" 컬럼과 같은 글자 표기 (R=읽기 전용, H=숨김, S=시스템, D=폴더, A=보관,
        /// T=임시, P=고정, U=고정 해제, L=링크, C=압축, O=오프라인, I=색인 안 함, E=암호화, M=액세스 시 회수).
        /// </summary>
        public static string FormatAttributes(FileAttributes attrs)
        {
            if (attrs == 0) return string.Empty;
            var sb = new StringBuilder(8);
            int a = (int)attrs;
            if ((a & 0x1) != 0) sb.Append('R');
            if ((a & 0x2) != 0) sb.Append('H');
            if ((a & 0x4) != 0) sb.Append('S');
            if ((a & 0x10) != 0) sb.Append('D');
            if ((a & 0x20) != 0) sb.Append('A');
            if ((a & 0x100) != 0) sb.Append('T');
            if ((a & 0x80000) != 0) sb.Append('P');
            if ((a & 0x100000) != 0) sb.Append('U');
            if ((a & 0x400) != 0) sb.Append('L');
            if ((a & 0x800) != 0) sb.Append('C');
            if ((a & 0x1000) != 0) sb.Append('O');
            if ((a & 0x2000) != 0) sb.Append('I');
            if ((a & 0x4000) != 0) sb.Append('E');
            if ((a & 0x400000) != 0) sb.Append('M');
            return sb.ToString();
        }

        /// <summary>
        /// 클라우드 자리 표시자(내용이 로컬에 없는 파일)인지. 셸 속성을 읽으면 다운로드가 시작될 수 있어
        /// 이런 파일은 건너뛴다 (OFFLINE / RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS).
        /// </summary>
        public static bool IsCloudPlaceholder(FileAttributes attrs)
            => ((int)attrs & (0x1000 | 0x40000 | 0x400000)) != 0;

        /// <summary>탐색기와 같은 "hh:mm:ss" 길이 표기. 24시간 이상은 시간을 그대로 늘린다.</summary>
        public static string FormatDuration(long ticks)
        {
            if (ticks <= 0) return string.Empty;
            var ts = TimeSpan.FromTicks(ticks);
            int hours = (int)ts.TotalHours;
            return $"{hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
        }
    }

    /// <summary>
    /// 저장되는 Details 컬럼 레이아웃 (표시 여부, 너비, 정렬, 그룹화).
    /// 설정의 "DetailsColumnsJson"에 JSON 문자열로 들어간다.
    /// Location/Git은 상황에 따라 자동 표시되므로 표시 여부를 저장하지 않는다.
    /// </summary>
    public sealed class DetailsColumnLayout
    {
        public Dictionary<string, bool> Visible { get; set; } = new();
        public Dictionary<string, double> Widths { get; set; } = new();
        public string SortBy { get; set; } = "Name";
        public bool SortAscending { get; set; } = true;
        public string GroupBy { get; set; } = "None";

        public bool IsVisible(DetailsColumnInfo column)
            => Visible.TryGetValue(column.Id, out var v) ? v : column.DefaultVisible;

        public double GetWidth(DetailsColumnInfo column)
            => Widths.TryGetValue(column.Id, out var w) && w >= column.MinWidth && w < 5000
                ? w
                : column.DefaultWidth;

        public string ToJson() => JsonSerializer.Serialize(this);

        public static DetailsColumnLayout FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new DetailsColumnLayout();
            try
            {
                var layout = JsonSerializer.Deserialize<DetailsColumnLayout>(json);
                if (layout == null) return new DetailsColumnLayout();
                layout.Visible ??= new();
                layout.Widths ??= new();
                if (string.IsNullOrEmpty(layout.SortBy)) layout.SortBy = "Name";
                if (string.IsNullOrEmpty(layout.GroupBy)) layout.GroupBy = "None";
                return layout;
            }
            catch (JsonException)
            {
                return new DetailsColumnLayout();
            }
        }
    }
}
