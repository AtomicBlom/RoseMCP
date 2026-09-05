using System;

using Microsoft.UI.Xaml;

namespace Rose.ProbeApp.WinUi
{
    /// <summary>
    /// The app entry point: on launch, show the one window.
    /// <para>
    /// The UWP probe navigates a Frame because a UWP app's content is a page. WinUI 3 has no
    /// ambient window -- <c>Window.Current</c> does not exist -- so the app makes one and holds it.
    /// That difference is the whole of #75, and having it in a fixture is the point of this app.
    /// </para>
    /// </summary>
    public partial class App : Application
    {
        private Window window;

        public App()
        {
            this.InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // A one-time, distinctly named first-chance exception during startup, before the window is
            // shown. Only a debugger attached from birth (issue #5) sees it; an attach that lands a beat
            // after activation has already missed OnLaunched, so this is what the startup-capture test
            // looks for to prove the debugger was present from the runtime's first breath.
            try
            {
                throw new RoseWinUiStartupException();
            }
            catch (RoseWinUiStartupException)
            {
                // Swallowed on purpose; the point is the first-chance notification, not the throw.
            }

            this.window = new MainWindow();
            this.window.Activate();
        }
    }

    /// <summary>The distinctively named exception thrown once during startup; the from-birth test looks
    /// for it in the event stream.</summary>
    internal sealed class RoseWinUiStartupException : Exception
    {
        public RoseWinUiStartupException()
            : base("rose winui startup")
        {
        }
    }
}
