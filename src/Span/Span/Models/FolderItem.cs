using System;
using System.Collections.Generic;

namespace Span.Models
{
    /// <summary>
    /// 폴더를 나타내는 데이터 모델.
    /// <see cref="IFileSystemItem"/>을 구현하며, 하위 파일과 폴더를 Children 컬렉션으로 통합한다.
    /// </summary>
    public class FolderItem : IFileSystemItem
    {
        private string _name = string.Empty;

        /// <summary>
        /// 폴더명.
        /// setter에서 NFC 정규화를 자동 적용하여 NFD 폴더명의 표시 깨짐을 방지한다.
        /// </summary>
        public string Name
        {
            get => _name;
            set => _name = FileItem.NfcNormalize(value);
        }

        /// <summary>폴더 전체 경로.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>마지막 수정 시각.</summary>
        public DateTime DateModified { get; set; }

        /// <summary>만든 날짜. 열거 시 함께 읽힌다 (원격/검색 결과는 MinValue).</summary>
        public DateTime DateCreated { get; set; }

        /// <summary>폴더 특성 (읽기 전용, 숨김 등). 열거 시 함께 읽힌다 (원격/검색 결과는 0).</summary>
        public System.IO.FileAttributes Attributes { get; set; }

        // Die drei Sammlungen entstehen erst beim ersten Zugriff. Eine Ordnerliste legt je Unterordner ein
        // FolderItem an und benutzt sie dort nie — vorher kamen auf jeden Eintrag vier zusätzliche Objekte
        // (bei 33.000 Unterordnern rund 130.000 Allokationen und einige MB, die nur der GC wieder einsammelte).
        private List<FileItem>? _files;
        private List<FolderItem>? _subFolders;
        private System.Collections.ObjectModel.ObservableCollection<IFileSystemItem>? _children;

        /// <summary>하위 파일 목록 (구조적 접근용).</summary>
        public List<FileItem> Files
        {
            get => _files ??= new List<FileItem>();
            set => _files = value;
        }

        /// <summary>하위 폴더 목록 (구조적 접근용).</summary>
        public List<FolderItem> SubFolders
        {
            get => _subFolders ??= new List<FolderItem>();
            set => _subFolders = value;
        }

        /// <summary>
        /// UI 바인딩용 통합 컬렉션. 파일과 폴더를 IFileSystemItem으로 합쳐서 ListView/ItemsControl에 바인딩한다.
        /// </summary>
        public System.Collections.ObjectModel.ObservableCollection<IFileSystemItem> Children
        {
            get => _children ??= new System.Collections.ObjectModel.ObservableCollection<IFileSystemItem>();
            set => _children = value;
        }

        /// <summary>숨김 폴더 여부.</summary>
        public bool IsHidden { get; set; }

        /// <summary>
        /// Read-Only 또는 System 속성 여부. Windows 탐색기가 desktop.ini를 해석하는 트리거.
        /// true일 때만 FolderIconService가 커스텀 아이콘 추출을 시도한다 (false는 즉시 글리프).
        /// </summary>
        public bool MaybeHasCustomIcon { get; set; }

        /// <summary>
        /// 하위 항목(파일/폴더) 존재 여부. 열거 시점에 경량 체크.
        /// Miller 컬럼 셰브론(▶) 표시 판단에 사용.
        /// </summary>
        public bool HasChildEntries { get; set; } = true;

        /// <summary>
        /// <see cref="HasChildEntries"/> ist noch die Annahme "hat Inhalt" und nicht geprüft. Die Ordnerliste
        /// setzt das für jeden Unterordner; geprüft wird erst, wenn die Zeile in der Miller-Ansicht erscheint
        /// (früher öffnete das Laden dafür jeden Unterordner). Bleibt false, wo es nichts zu prüfen gibt
        /// (FTP/SFTP, Archive, Suchergebnisse, Freigaben).
        /// </summary>
        public bool ChildProbePending { get; set; }

        /// <summary>현재 아이콘 팩의 폴더 아이콘 글리프. IconService.Current를 통해 런타임에 결정된다.</summary>
        public string IconGlyph => Span.Services.IconService.Current?.FolderGlyph ?? "\uED53";
    }
}
