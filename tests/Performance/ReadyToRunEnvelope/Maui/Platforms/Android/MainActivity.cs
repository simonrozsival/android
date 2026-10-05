using Android.App;
using Android.Content.PM;
using Android.Util;
using Microsoft.Maui;

namespace ReadyToRunEnvelope.Maui;

[Activity (Name = "com.github.copilot.r2renvelope.maui.MainActivity", Theme = "@style/Maui.MainTheme.NoActionBar",
	MainLauncher = true, Exported = true,
	ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
		ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
	bool ready;

	public override void OnWindowFocusChanged (bool hasFocus)
	{
		base.OnWindowFocusChanged (hasFocus);
		if (!hasFocus || ready) {
			return;
		}
		ready = true;
		Window?.DecorView.Post (() => {
			Log.Info ("R2REnvelopeStartup", "READY");
			ReportFullyDrawn ();
		});
	}
}
