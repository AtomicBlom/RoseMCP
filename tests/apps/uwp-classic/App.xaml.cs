using System;

using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Rose.ProbeApp.UwpClassic
{
    /// <summary>The app entry point: on launch, host a MainPage in a frame and show the window.</summary>
    sealed partial class App : Application
    {
        public App()
        {
            this.InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            // A one-time, distinctly named first-chance exception during startup, before the window is
            // shown. Only a debugger attached from birth (issue #5) sees it; an attach that lands a beat
            // after activation has already missed OnLaunched, so this is what the startup-capture test
            // looks for to prove the debugger was present from the runtime's first breath.
            try
            {
                throw new RoseUwpStartupException();
            }
            catch (RoseUwpStartupException)
            {
                // Swallowed on purpose; the point is the first-chance notification, not the throw.
            }

            if (!(Window.Current.Content is Frame frame))
            {
                frame = new Frame();
                Window.Current.Content = frame;
            }

            if (frame.Content == null)
            {
                frame.Navigate(typeof(MainPage), e.Arguments);
            }

            Window.Current.Activate();
        }
    }

    /// <summary>The distinctively named exception thrown once during startup; the from-birth test looks
    /// for it in the event stream.</summary>
    internal sealed class RoseUwpStartupException : Exception
    {
        public RoseUwpStartupException()
            : base("rose uwp startup")
        {
        }
    }
}
