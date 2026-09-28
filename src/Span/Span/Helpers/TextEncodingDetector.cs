using System;
using System.Buffers;
using System.Text;
using System.Text.Unicode;

namespace Span.Helpers;

/// <summary>
/// BOM 없는 텍스트 파일의 인코딩을 고른다. UTF-8이면 UTF-8, 아니면 OS ANSI 코드페이지
/// (한국어 Windows = CP949, 중국어 간체 = 936/GBK, 일본어 = 932/Shift-JIS). 읽기 전용.
///
/// 배경(Issue #69): 미리보기 로더가 BOM이 없으면 UTF-8로만 읽어 CP949 자막(.srt/.smi)이나
/// ANSI로 저장한 텍스트가 '�'로 가득 찼다. .NET Core 이후 Encoding.Default가 UTF-8로 바뀌어
/// .NET Framework 시절의 암묵적 ANSI 폴백이 사라진 것이다 — ZipEntryNameEncoding과 같은 문제.
///
/// 판정 기준은 ZipEntryNameEncoding과 다르다. 그쪽은 파일명이라 "잘못된 UTF-8이 하나라도 있으면
/// ANSI"인데, 본문에 같은 규칙을 쓰면 대부분 UTF-8이고 깨진 바이트가 몇 개 섞인 파일(바이너리
/// 조각이 낀 로그 등)이 통째로 ANSI로 해석돼 전부 깨진다. 그래서 잘못된 시퀀스가 비ASCII
/// 바이트의 5% 미만이면 UTF-8로 본다. 실측:
///   UTF-8 한국어·중국어      0.0%      UTF-8 + 깨진 바이트 3개   0.04%
///   CP949 한국어            61.5%      GBK 중국어              76.5%
///   Shift-JIS 일본어        82.2%
/// 두 부류 사이가 넓어 5%에서 확실히 갈린다.
///
/// 코드페이지 출처도 다르다. 여기서는 Encoding.GetEncoding(0) = 시스템 로캘("유니코드를
/// 지원하지 않는 프로그램용 언어")을 쓴다. 레거시 앱은 이 코드페이지로 파일을 저장했다.
/// ZipEntryNameEncoding의 CultureInfo.CurrentCulture는 사용자의 "형식" 설정이라, 한국어
/// 시스템에 영어(미국) 형식을 쓰면 1252가 돼 CP949 파일을 못 읽는다.
/// </summary>
internal static class TextEncodingDetector
{
    /// <summary>잘못된 바이트를 U+FFFD로 치환하는 UTF-8. 판정이 UTF-8일 때 쓴다.</summary>
    private static readonly UTF8Encoding Utf8Lax = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>UTF-8이 아닐 때 쓸 OS ANSI 코드페이지. 쓸 수 없으면 null(UTF-8만 쓴다).</summary>
    private static readonly Lazy<Encoding?> Ansi = new(ResolveAnsi);

    /// <summary>
    /// BOM이 없을 때 쓸 인코딩을 고른다. head는 파일 앞부분.
    /// isWholeFile이 false면 끝에서 잘린 멀티바이트 문자를 오류로 세지 않는다 — 앞부분만 읽으면
    /// 경계에서 한 글자가 잘리기 마련이고, 그걸 오류로 세면 멀쩡한 UTF-8을 ANSI로 오판한다.
    /// </summary>
    public static Encoding DetectNoBom(ReadOnlySpan<byte> head, bool isWholeFile)
    {
        var ansi = Ansi.Value;
        if (ansi is null) return Utf8Lax;

        int nonAscii = 0;
        foreach (byte b in head)
            if (b >= 0x80) nonAscii++;
        if (nonAscii == 0) return Utf8Lax;   // 순수 ASCII는 어느 쪽으로 읽어도 같다

        int invalid = 0;
        Span<char> scratch = stackalloc char[2048];
        var src = head;
        while (!src.IsEmpty)
        {
            var status = Utf8.ToUtf16(src, scratch, out int read, out _,
                replaceInvalidSequences: false, isFinalBlock: isWholeFile);

            // NeedMoreData = 끝에서 잘린 문자(isWholeFile이 false일 때만 나온다). 세지 않는다.
            if (status is OperationStatus.Done or OperationStatus.NeedMoreData)
                break;

            if (status == OperationStatus.DestinationTooSmall)
            {
                if (read == 0) break;
                src = src[read..];
                continue;
            }

            // InvalidData: read는 잘못된 시퀀스 직전까지다. 그 바이트 하나를 건너뛰고 계속 센다.
            invalid++;
            if (invalid * 20 >= nonAscii) return ansi;   // 이미 5%를 넘었다 — 끝까지 볼 필요 없다
            src = src[(read + 1)..];
        }
        return Utf8Lax;
    }

    private static Encoding? ResolveAnsi()
    {
        try
        {
            // .NET Core는 레거시 코드페이지를 기본으로 싣지 않는다. 등록은 멱등이다.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // 공급자를 등록하면 GetEncoding(0)은 시스템 ANSI 코드페이지(GetACP)를 돌려준다.
            var encoding = Encoding.GetEncoding(0);

            // ANSI가 UTF-8인 환경("Beta: Use Unicode UTF-8 for worldwide language support")에서는
            // 폴백이 의미가 없다 — UTF-8 경로가 이미 처리한다.
            if (encoding.CodePage != 65001)
                return encoding;
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"[TextEncoding] ANSI codepage unavailable, UTF-8 only: {ex.GetType().Name}");
        }
        return null;
    }
}
