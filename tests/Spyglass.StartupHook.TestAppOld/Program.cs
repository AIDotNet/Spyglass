// Stands in for an app on an Avalonia version the hook does not support (11.0.13 is shipped, never started):
// it only has to be alive for a few seconds so the test can check that nothing attached and nothing broke.
Console.WriteLine("old app running");
await Task.Delay(TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("TESTAPP_SECONDS"), out var s) ? s : 30));
