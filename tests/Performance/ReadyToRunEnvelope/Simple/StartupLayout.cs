using Android.Content;
using Android.Views;
using Android.Widget;

namespace ReadyToRunEnvelope;

class StartupLayout : LinearLayout
{
	public StartupLayout (Context context) : base (context)
	{
	}

	public override WindowInsets OnApplyWindowInsets (WindowInsets insets)
	{
		if (insets == null) {
			throw new System.ArgumentNullException (nameof (insets));
		}
		base.OnApplyWindowInsets (insets);
		if (System.OperatingSystem.IsAndroidVersionAtLeast (30)) {
			using var bars = insets.GetInsets (WindowInsets.Type.SystemBars () | WindowInsets.Type.DisplayCutout ());
			SetPadding (bars.Left, bars.Top, bars.Right, bars.Bottom);
		} else {
#pragma warning disable CS0618
			SetPadding (insets.SystemWindowInsetLeft, insets.SystemWindowInsetTop, insets.SystemWindowInsetRight, insets.SystemWindowInsetBottom);
#pragma warning restore CS0618
		}
		return insets;
	}
}
