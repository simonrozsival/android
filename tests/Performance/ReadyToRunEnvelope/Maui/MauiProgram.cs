using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace ReadyToRunEnvelope.Maui;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp () => MauiApp.CreateBuilder ().UseMauiApp<App> ().Build ();
}

public class App : Application
{
	protected override Window CreateWindow (IActivationState activationState) => new Window (new ContentPage {
		Content = new Label {
			Text = "MAUI ReadyToRun envelope experiment",
			HorizontalOptions = LayoutOptions.Center,
			VerticalOptions = LayoutOptions.Center,
		},
	});
}
