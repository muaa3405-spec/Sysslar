using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Threading;

namespace Tasky.Helpers;

/// <summary>
/// Short property animations (slides, fades, zooms) written once for every window.
/// Works on any Avalonia object — including transforms, which Avalonia's own
/// animation API cannot target. The property ends on its final value. Starting a new
/// animation of the same property stops the running one, whose task then returns false.
/// </summary>
public static class Animator
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / 60);

    /// <summary>The animation currently driving each property of each object.</summary>
    private static readonly ConditionalWeakTable<AvaloniaObject, Dictionary<AvaloniaProperty, Action>> Running = new();

    public static Task<bool> Animate(AvaloniaObject target, AvaloniaProperty<double> property,
                                     double from, double to, TimeSpan duration, Easing? easing = null)
        => Run(target, property, from, to, duration, easing, (a, b, t) => a + (b - a) * t);

    public static Task<bool> Animate(AvaloniaObject target, AvaloniaProperty<Thickness> property,
                                     Thickness from, Thickness to, TimeSpan duration, Easing? easing = null)
        => Run(target, property, from, to, duration, easing, (a, b, t) => new Thickness(
               a.Left + (b.Left - a.Left) * t, a.Top + (b.Top - a.Top) * t,
               a.Right + (b.Right - a.Right) * t, a.Bottom + (b.Bottom - a.Bottom) * t));

    /// <returns>True when the fade finished; false when another animation took over.</returns>
    public static Task<bool> Fade(Visual target, double from, double to, TimeSpan duration, Easing? easing = null)
        => Animate(target, Visual.OpacityProperty, from, to, duration, easing);

    private static Task<bool> Run<T>(AvaloniaObject target, AvaloniaProperty<T> property, T from, T to,
                                     TimeSpan duration, Easing? easing, Func<T, T, double, T> lerp)
    {
        var running = Running.GetOrCreateValue(target);
        if (running.Remove(property, out var stopPrevious)) stopPrevious();

        // Reduce motion: jump straight to the end value
        if (!Motion.Enabled) duration = TimeSpan.Zero;

        var done  = new TaskCompletionSource<bool>();
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = FrameInterval };
        void Stop(bool finished) { timer.Stop(); done.TrySetResult(finished); }
        running[property] = () => Stop(finished: false);

        target.SetValue(property, from);
        timer.Tick += (_, _) =>
        {
            double progress = duration <= TimeSpan.Zero ? 1 : Math.Min(1, clock.Elapsed / duration);
            target.SetValue(property, lerp(from, to, easing?.Ease(progress) ?? progress));
            if (progress < 1) return;
            running.Remove(property);
            Stop(finished: true);
        };
        timer.Start();
        return done.Task;
    }
}
