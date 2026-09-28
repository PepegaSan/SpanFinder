using Microsoft.Windows.AppLifecycle;
using System;

namespace Span;

class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Issue #36: ColorCode 등 라이브러리의 Regex catastrophic backtracking이 UI 스레드를
        // 멈추지 않도록 기본 매치 타임아웃을 1초로 건다.
        //
        // 반드시 프로세스에서 가장 먼저 실행돼야 한다. Regex는 기본 타임아웃을 처음 쓰일 때
        // 정적 필드에 읽어 고정하므로, 어떤 코드든 Regex를 먼저 만들면 이후 SetData는 무시된다.
        // 이전에는 App 생성자 중간에 있었는데 그 앞의 Sentry 초기화(CrashReportingService)가
        // Regex를 먼저 만들어서, 이 타임아웃은 출하 빌드에서 한 번도 적용된 적이 없었다
        // (실측: 무한). App 생성자의 시작 로그가 실제 적용값을 기록한다.
        AppDomain.CurrentDomain.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", TimeSpan.FromSeconds(1));

        WinRT.ComWrappersSupport.InitializeComWrappers();

        var isRedirect = DecideRedirection();
        if (!isRedirect)
        {
            Microsoft.UI.Xaml.Application.Start((p) =>
            {
                var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }

        return 0;
    }

    private static bool DecideRedirection()
    {
        var appInstance = AppInstance.FindOrRegisterForKey("SPAN_FINDER_MAIN");

        if (appInstance.IsCurrent)
            return false; // 첫 인스턴스 — 정상 실행

        // 기존 인스턴스로 활성화 리다이렉트
        var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        appInstance.RedirectActivationToAsync(activatedArgs).AsTask().Wait();
        return true; // 현재 프로세스 종료
    }
}
