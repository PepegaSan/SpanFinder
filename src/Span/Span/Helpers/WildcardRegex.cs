using System;
using System.Text.RegularExpressions;

namespace Span.Helpers;

/// <summary>
/// 와일드카드 이름 패턴(* = 0개 이상 문자, ? = 정확히 1개 문자)을 전체 이름 매칭 Regex로 바꾼다.
/// 필터 바(FolderViewModel.MatchesFilter)와 검색(SearchQueryParser → SearchFilter)이 같이 쓴다.
///
/// 엔진은 NonBacktracking이다. 와일드카드가 여럿인 패턴(*a*a*a*b)은 백트래킹 엔진에서 다항
/// 시간이라 긴 이름에 수백 ms가 걸리고, Regex 기본 타임아웃(Issue #36, 1초)에 걸리면 예외가 된다.
/// 두 호출부 모두 IsMatch를 try 밖에서 부르므로 그 예외는 크래시다. NonBacktracking은 선형
/// 시간이라 백트래킹 자체가 없다. 와일드카드가 만드는 .*와 .는 두 엔진에서 의미가 같다.
/// </summary>
internal static class WildcardRegex
{
    public static Regex Create(string wildcard)
    {
        var pattern = "^" + Regex.Escape(wildcard).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
        }
        catch (NotSupportedException)
        {
            // NonBacktracking은 오토마톤 크기에 상한이 있어 긴 패턴은 생성이 실패한다(기본 상한에서
            // '*' + 199자부터). Program.Main이 상한을 올려 두지만, 그보다 큰 패턴이나 Main을 거치지
            // 않는 호출(단위 테스트)은 여기로 온다. 2.0.6까지와 같은 백트래킹 엔진으로 되돌린다.
            // 타임아웃은 명시적으로 무한 — 기본 1초를 물려받으면 위의 크래시 경로가 다시 열린다.
            DebugLogger.Log($"[WildcardRegex] NonBacktracking unavailable (pattern {pattern.Length} chars), using backtracking");
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, Regex.InfiniteMatchTimeout);
        }
    }
}
