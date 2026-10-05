using System.Runtime.CompilerServices;
using Android.App;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace ReadyToRunEnvelope;

[Activity (Name = "com.github.copilot.r2renvelope.MainActivity", Theme = "@style/R2REnvelopeTheme", MainLauncher = true, Exported = true)]
public class MainActivity : Activity
{
	bool ready;

	protected override void OnCreate (Bundle savedInstanceState)
	{
		base.OnCreate (savedInstanceState);
		var layout = new StartupLayout (this) { Orientation = Orientation.Vertical };
		var text = new TextView (this) { Text = CreateStartupText (), TextSize = 24 };
		layout.AddView (text);
#if R2R_DIAGNOSTIC
		var finish = new Button (this) { Text = "Finish diagnostic trace" };
		finish.Click += (sender, args) => System.Environment.Exit (0);
		layout.AddView (finish);
#endif
		SetContentView (layout);
		layout.RequestApplyInsets ();
#if R2R_DIAGNOSTIC
		int gc = DiagnosticGc ();
		string generic = DiagnosticGeneric ();
		int exception = DiagnosticException ();
		if (gc != 1024 || generic != "ELF" || exception != 17) {
			throw new System.InvalidOperationException ($"ELF diagnostic checks failed: GC={gc}, GENERIC={generic}, EH={exception}");
		}
		Log.Info ("R2REnvelopeStartup", $"DIAGNOSTIC CHECKS PASSED GC={gc} GENERIC={generic} EH={exception}");
#endif
	}

	[MethodImpl (MethodImplOptions.NoInlining)]
	static string CreateStartupText () => "ReadyToRun envelope experiment";

#if R2R_DIAGNOSTIC
	[MethodImpl (MethodImplOptions.NoInlining)]
	static int DiagnosticGc ()
	{
		var values = new byte [32] [];
		for (int i = 0; i < values.Length; i++) {
			values [i] = new byte [i + 1];
			values [i] [0] = (byte)i;
		}
		System.GC.Collect (2, System.GCCollectionMode.Forced, blocking: true, compacting: true);
		int result = 0;
		foreach (var value in values) {
			result += value [0] + value.Length;
		}
		return result;
	}

	[MethodImpl (MethodImplOptions.NoInlining)]
	static string DiagnosticGeneric () => DiagnosticIdentity ("ELF");

	[MethodImpl (MethodImplOptions.NoInlining)]
	static T DiagnosticIdentity<T> (T value) => value;

	[MethodImpl (MethodImplOptions.NoInlining)]
	static int DiagnosticException ()
	{
		try {
			throw new System.InvalidOperationException ("ELF diagnostic exception");
		} catch (System.InvalidOperationException e) {
			return e.Message == "ELF diagnostic exception" ? 17 : -1;
		}
	}
#endif

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
