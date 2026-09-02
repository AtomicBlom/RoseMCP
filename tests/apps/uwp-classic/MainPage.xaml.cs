using System;

using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Rose.ProbeApp.UwpClassic
{
    /// <summary>
    /// The one page. It has named, inspectable elements (RootGrid, Panel, Pane, Caption, Counter) for
    /// the visual-tree and property tests, and a Tick method the debugger tests can trace, break on,
    /// and read locals from -- which also throws a distinctively named exception so exception capture
    /// can be exercised against a UWP target, the way the console probe's Beat does.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private int ticks;

        public MainPage()
        {
            this.InitializeComponent();
            this.timer.Interval = TimeSpan.FromMilliseconds(500);
            this.timer.Tick += this.OnTimerTick;
            this.timer.Start();
        }

        private void OnTimerTick(object sender, object e)
        {
            this.Tick();
        }

        private void Tick()
        {
            this.ticks++;
            this.Counter.Text = "ticks: " + this.ticks;

            try
            {
                throw new RoseUwpProbeException(this.ticks);
            }
            catch (RoseUwpProbeException)
            {
                // Swallowed on purpose; an attached debugger sees it first-chance.
            }
        }
    }

    /// <summary>The distinctively named exception the live-app tests look for in the event stream.</summary>
    internal sealed class RoseUwpProbeException : Exception
    {
        public RoseUwpProbeException(int tick)
            : base("rose uwp probe tick " + tick)
        {
            this.Tick = tick;
        }

        public int Tick { get; }
    }
}
