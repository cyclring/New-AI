using System.Windows;

namespace WorkControl;

public partial class App : Application
{
    private Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        instance = new Mutex(false, @"Local\WorkControl.Assi");
        bool owns;
        try { owns = instance.WaitOne(0); }
        catch (AbandonedMutexException) { owns = true; }
        if (!owns)
        {
            instance.Dispose();
            instance = null;
            MessageBox.Show("이미 열려 있는 어시 창을 사용하세요.", "AI 어시스턴트");
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
