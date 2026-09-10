using System.Reflection;
using HotMod.Contracts;

namespace HotMod.Logic;

public sealed class HotLogic : IHotLogic
{
    private IHotHost? _host;
    private HotReloadContext? _context;
    private readonly CancellationTokenSource _generationStopping = new();

    public HotLogicManifest Manifest { get; } = new(
        "hotmod.template",
        "HotMod Template",
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev",
        HotContract.ContractVersion,
        new Dictionary<string, string>
        {
            ["combat.turn"] = "Logs a message when the shell trampoline observes a combat turn."
        });

    public void Initialize(IHotHost host, HotReloadContext context)
    {
        _host = host;
        _context = context;
        host.Log(HotLogLevel.Information, "HotMod logic generation initialized", new Dictionary<string, string>
        {
            ["generation"] = context.Generation.ToString(),
            ["sourceAssemblyPath"] = context.SourceAssemblyPath,
            ["shadowAssemblyPath"] = context.ShadowAssemblyPath
        });
    }

    public HotLogicResult OnHook(HotHookContext context)
    {
        if (context.HookId != "combat.turn")
        {
            return new HotLogicResult(false, []);
        }

        var message = $"HotMod logic generation {context.Generation} observed combat.turn.";
        _host?.Log(HotLogLevel.Information, message, new Dictionary<string, string>
        {
            ["hookId"] = context.HookId
        });
        return new HotLogicResult(true, [message]);
    }

    public void Dispose()
    {
        _generationStopping.Cancel();
        _generationStopping.Dispose();
        _host?.Log(HotLogLevel.Information, "HotMod logic generation disposed", new Dictionary<string, string>
        {
            ["generation"] = (_context?.Generation ?? 0).ToString()
        });
    }
}
