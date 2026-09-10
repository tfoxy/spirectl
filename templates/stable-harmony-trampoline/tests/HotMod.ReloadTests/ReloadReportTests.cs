using System.Text.Json;
using HotMod.Shell.Diagnostics;
using HotMod.Shell.Runtime;
using Xunit;

namespace HotMod.ReloadTests;

public sealed class ReloadReportTests
{
    [Fact]
    public void LoadedReportSerializesWithCamelCaseFields()
    {
        var requestedAt = DateTimeOffset.Parse("2026-04-24T00:00:00Z");
        var report = ReloadReport.Loaded(
            generation: 4,
            requestedAt: requestedAt,
            sourceAssemblyPath: "/mods/HotMod/hot-reload/HotMod.Logic.dll",
            shadowAssemblyPath: "/mods/HotMod/hot-reload/.shadow/generation-4/HotMod.Logic.dll",
            contractVersion: 0,
            logicAssemblyName: "HotMod.Logic",
            entryType: "HotMod.Logic.HotLogic",
            previousGeneration: 3,
            previousDisposed: true,
            previousUnloadRequested: true,
            previousCollected: true,
            durationMs: 42,
            warnings: Array.Empty<ReloadWarning>());

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(report, ReloadJson.Options));
        var root = document.RootElement;

        Assert.Equal("loaded", root.GetProperty("status").GetString());
        Assert.Equal(4, root.GetProperty("generation").GetInt32());
        Assert.Equal("/mods/HotMod/hot-reload/HotMod.Logic.dll", root.GetProperty("sourceAssemblyPath").GetString());
        Assert.Equal(3, root.GetProperty("previousGeneration").GetInt32());
        Assert.True(root.GetProperty("previousDisposed").GetBoolean());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("warnings").ValueKind);
    }

    [Fact]
    public void FailedReportIncludesStructuredError()
    {
        var report = ReloadReport.Failed(
            requestedAt: DateTimeOffset.Parse("2026-04-24T00:00:00Z"),
            sourceAssemblyPath: "/mods/HotMod/hot-reload/HotMod.Logic.dll",
            shadowAssemblyPath: null,
            previousGeneration: 3,
            durationMs: 12,
            error: new ReloadError(
                ReloadErrorCode.ReloadContractVersionMismatch,
                ReloadPhase.Contract,
                "HotMod.Logic was built for contract version 9, but the shell supports version 0.",
                ExceptionType: null,
                ExceptionMessage: null,
                RestartRequired: false));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(report, ReloadJson.Options));
        var root = document.RootElement;
        var error = root.GetProperty("error");

        Assert.Equal("failed", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("previousRemainsActive").GetBoolean());
        Assert.Equal("reload_contract_version_mismatch", error.GetProperty("code").GetString());
        Assert.Equal("contract", error.GetProperty("phase").GetString());
        Assert.False(error.GetProperty("restartRequired").GetBoolean());
    }

    [Fact]
    public void ReloadLogWritesJsonLineWithMessageAndData()
    {
        using var writer = new StringWriter();
        ReloadLog.WriteInfo(writer, DateTimeOffset.Parse("2026-04-24T00:00:00Z"), "loaded", new { generation = 1 });

        using var document = JsonDocument.Parse(writer.ToString());
        var root = document.RootElement;

        Assert.Equal("2026-04-24T00:00:00+00:00", root.GetProperty("timestamp").GetString());
        Assert.Equal("information", root.GetProperty("level").GetString());
        Assert.Equal("loaded", root.GetProperty("message").GetString());
        Assert.Equal(1, root.GetProperty("data").GetProperty("generation").GetInt32());
    }
}
