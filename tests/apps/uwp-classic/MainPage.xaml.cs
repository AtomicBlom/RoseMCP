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

        /// <summary>Held across removals, because the tree stops being where it can be found.</summary>
        private UIElement transient;

        public MainPage()
        {
            this.InitializeComponent();
            this.timer.Interval = TimeSpan.FromMilliseconds(500);
            this.timer.Tick += this.OnTimerTick;
            this.transient = this.Transient;
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
            this.CycleTransient();

            try
            {
                throw new RoseUwpProbeException(this.ticks);
            }
            catch (RoseUwpProbeException)
            {
                // Swallowed on purpose; an attached debugger sees it first-chance.
            }
        }

        /// <summary>
        /// Ticks between the Transient border leaving the tree and coming back, and between coming
        /// back and leaving again. Present for most of the cycle, because a test has to be able to
        /// find and select it before it can watch it go.
        /// </summary>
        private const int TransientAbsentTicks = 2;
        private const int TransientCycleTicks = 10;

        private void CycleTransient()
        {
            int phase = this.ticks % TransientCycleTicks;

            if (phase == 0)
            {
                this.RemoveTransient();
            }
            else if (phase == TransientAbsentTicks)
            {
                this.RestoreTransient();
            }
        }

        /// <summary>
        /// Takes Transient out of the visual tree. The exception is how a test knows it has happened:
        /// the live-app suite already waits on named exceptions in the event stream, and there is no
        /// other channel out of this app.
        /// </summary>
        private void RemoveTransient()
        {
            if (this.transient == null || !this.Panel.Children.Contains(this.transient))
            {
                return;
            }

            this.Panel.Children.Remove(this.transient);

            try
            {
                throw new RoseUwpTransientRemovedException(this.ticks);
            }
            catch (RoseUwpTransientRemovedException)
            {
                // Swallowed on purpose; an attached debugger sees it first-chance.
            }
        }

        /// <summary>
        /// Puts the same instance back, which is deliberate. An element unparented and re-added is
        /// what virtualization and a rebuilt panel do, and it is the case where a handle stays valid
        /// across a removal -- so anything that clears a selection on removal has to be right about
        /// this one too rather than only about elements that die.
        /// </summary>
        private void RestoreTransient()
        {
            if (this.transient == null || this.Panel.Children.Contains(this.transient))
            {
                return;
            }

            this.Panel.Children.Add(this.transient);
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

    /// <summary>
    /// Thrown when the Transient border leaves the visual tree, which is how a test knows a removal
    /// happened. The event stream is the only channel out of this app, and the live-app suite already
    /// waits on named exceptions in it.
    /// </summary>
    internal sealed class RoseUwpTransientRemovedException : Exception
    {
        public RoseUwpTransientRemovedException(int tick)
            : base("rose uwp transient removed at tick " + tick)
        {
            this.Tick = tick;
        }

        public int Tick { get; }
    }
}
