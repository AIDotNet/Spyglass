using Avalonia.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>Avalonia log capture: logs/get, logs/configure, logs/entries and the MCP logs_read tool.</summary>
[Collection("app")]
public class LogsTests(AppFixture app)
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    async Task<int> Find(string query) => Assert.Single((await app.Session.SearchAsync(new SearchParams(query))).Matches).Id;

    /// <summary>Logs through Avalonia's public logger on the UI thread, exactly as Avalonia's own code does.</summary>
    Task<bool> Log(LogEventLevel level, string area, object? source, string template, params object?[] values) =>
        app.OnUi(() =>
        {
            if (!Logger.TryGet(level, area, out var logger)) return false;
            switch (values.Length)   // ParametrizedLogger has typed overloads, not params
            {
                case 0: logger.Log(source, template); break;
                case 1: logger.Log(source, template, values[0]); break;
                default: logger.Log(source, template, values[0], values[1]); break;
            }
            return true;
        });

    async Task<long> Cursor() => (await app.Session.GetLogsAsync(limit: 1)).Entries.LastOrDefault()?.Seq ?? 0;

    [Fact]
    public void Capability_is_declared() => Assert.True(app.Session.Has(Capabilities.Logs));

    [Fact]
    public async Task Warning_is_captured_with_area_level_message_and_source_element()
    {
        var since = await Cursor();
        // x:Name is a property in Debug builds with Spyglass.HotReload (FindNameScope): read it on the UI thread.
        var save = await app.OnUi(() => app.Window.SaveButton);
        Assert.True(await Log(LogEventLevel.Warning, LogArea.Layout, save,
            "Measure of {Control} took {Ms} ms", save, 42));

        var r = await app.Session.GetLogsAsync(since);
        var e = Assert.Single(r.Entries);
        Assert.Equal("Layout", e.Area);
        Assert.Equal("Warning", e.Level);
        Assert.Equal("Measure of Button#SaveButton took 42 ms", e.Message);
        Assert.Equal(await Find("#SaveButton"), e.SourceId);
        Assert.Equal("Button#SaveButton", e.SourceType);
        Assert.True(e.Seq > since);
        Assert.Equal("Warning", r.MinLevel);
        Assert.Null(r.Areas);
    }

    [Fact]
    public async Task Startup_binding_warnings_are_in_the_log_and_binding_errors_still_work()
    {
        var broken = await Find("#Broken");
        var r = await app.Session.GetLogsAsync(area: "binding", limit: 2000);   // area match is case-insensitive
        Assert.Contains(r.Entries, e => e.Area == "Binding" && e.Level == "Warning" && e.SourceId == broken && e.Message.Contains("FullNmae"));

        var errors = await app.Session.GetBindingErrorsAsync(broken);
        Assert.Equal("User.FullNmae", Assert.Single(errors).Path);
    }

    [Fact]
    public async Task Min_level_and_areas_control_what_is_recorded()
    {
        try
        {
            // Default Warning: Avalonia is told Debug is off, so it does not even format the message.
            Assert.False(await Log(LogEventLevel.Debug, LogArea.Layout, null, "not recorded"));

            await app.Session.ConfigureLogsAsync("Debug");
            var since = await Cursor();
            Assert.True(await Log(LogEventLevel.Debug, LogArea.Layout, null, "debug {N}", 1));
            Assert.True(await Log(LogEventLevel.Error, LogArea.Visual, null, "error {N}", 2));
            var r = await app.Session.GetLogsAsync(since);
            Assert.Equal("Debug", r.MinLevel);
            Assert.Equal(["debug 1", "error 2"], r.Entries.Select(e => e.Message).Where(m => m is "debug 1" or "error 2").ToArray());

            // logs/get filters without changing what is recorded
            var errorsOnly = await app.Session.GetLogsAsync(since, minLevel: "Error");
            Assert.All(errorsOnly.Entries, e => Assert.True(e.Level is "Error" or "Fatal"));
            Assert.Contains(errorsOnly.Entries, e => e.Message == "error 2");
            Assert.Equal("Visual", Assert.Single((await app.Session.GetLogsAsync(since, area: "Visual")).Entries, e => e.Message == "error 2").Area);

            await app.Session.ConfigureLogsAsync("Warning", ["Visual"]);
            since = await Cursor();
            await Log(LogEventLevel.Warning, LogArea.Layout, null, "layout warning");
            await Log(LogEventLevel.Warning, LogArea.Visual, null, "visual warning");
            r = await app.Session.GetLogsAsync(since);
            Assert.Equal(["Visual"], r.Areas);
            Assert.DoesNotContain(r.Entries, e => e.Message == "layout warning");
            Assert.Contains(r.Entries, e => e.Message == "visual warning");

            await Assert.ThrowsAsync<SpyglassProtocolException>(() => app.Session.ConfigureLogsAsync("Loud"));
        }
        finally
        {
            await app.Session.ConfigureLogsAsync("Warning");
        }
        Assert.False(await Log(LogEventLevel.Debug, LogArea.Layout, null, "not recorded"));
    }

    [Fact]
    public async Task Cursor_and_limit()
    {
        var since = await Cursor();
        for (var i = 1; i <= 3; i++) await Log(LogEventLevel.Warning, LogArea.Control, null, "cursor test {N}", i);

        var all = await app.Session.GetLogsAsync(since);
        Assert.Equal(["cursor test 1", "cursor test 2", "cursor test 3"], all.Entries.Select(e => e.Message).ToArray());   // oldest first
        Assert.True(all.Entries.Zip(all.Entries.Skip(1)).All(p => p.First.Seq < p.Second.Seq));

        Assert.Empty((await app.Session.GetLogsAsync(all.Entries[^1].Seq)).Entries);

        var newest = await app.Session.GetLogsAsync(since, limit: 2);
        Assert.Equal(["cursor test 2", "cursor test 3"], newest.Entries.Select(e => e.Message).ToArray());
        Assert.Equal(1, newest.Omitted);
    }

    [Fact]
    public async Task New_entries_are_pushed()
    {
        var pushed = new TaskCompletionSource<LogEntryDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(LogEntriesParams p)
        {
            if (p.Entries.FirstOrDefault(e => e.Message == "pushed warning") is { } e) pushed.TrySetResult(e);
        }
        app.Session.LogEntries += On;
        try
        {
            var since = await Cursor();
            await Log(LogEventLevel.Warning, LogArea.Property, await app.OnUi(() => app.Window.NameBox), "pushed warning");
            var e = await pushed.Task.WaitAsync(Timeout);
            Assert.True(e.Seq > since);
            Assert.Equal("Property", e.Area);
            Assert.Equal(await Find("#NameBox"), e.SourceId);
        }
        finally { app.Session.LogEntries -= On; }
    }

    [Fact]
    public async Task Mcp_logs_read_with_cursor_and_filters()
    {
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(app.Hub.BaseUri, "mcp"),
        }));
        async Task<string> Call(Dictionary<string, object?> args) =>
            string.Join("\n", (await client.CallToolAsync("logs_read", args)).Content.OfType<TextContentBlock>().Select(t => t.Text));

        await Log(LogEventLevel.Warning, LogArea.Layout, await app.OnUi(() => app.Window.SaveButton), "Mcp visible {What}", "warning");
        var text = await Call(new());
        Assert.Matches(@"\(cursor: \d+\)", text);
        Assert.Matches(@"\d\d:\d\d:\d\d\.\d{3} WRN \[Layout\] Mcp visible warning  ← Button#SaveButton \[e\d+\]", text);

        var cursor = long.Parse(System.Text.RegularExpressions.Regex.Match(text, @"cursor: (\d+)").Groups[1].Value);
        Assert.Contains("No new log entries since cursor", await Call(new() { ["since"] = cursor }));

        await Log(LogEventLevel.Warning, LogArea.Visual, null, "Mcp second");
        var next = await Call(new() { ["since"] = cursor, ["area"] = "Visual" });
        Assert.Contains("1 log entry", next);
        Assert.Contains("[Visual] Mcp second", next);
        Assert.DoesNotContain("Mcp visible", next);

        Assert.Contains("No new log entries since cursor", await Call(new() { ["since"] = cursor, ["minLevel"] = "Error" }));
        Assert.Contains("Debug entries are not available", await Call(new() { ["minLevel"] = "Debug", ["since"] = cursor }));
    }
}
