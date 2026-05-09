using System;
using AkuItemSets.Services;
using AkuItemSets.Windows;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace AkuItemSets;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/akuis";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("AkuItemSets");
    private readonly MainWindow mainWindow;

    public Configuration Configuration { get; }
    public ItemSetRepository ItemSetRepository { get; }
    public ItemCollectionScanner Scanner { get; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        ItemSetRepository = new ItemSetRepository(DataManager);
        Scanner = new ItemCollectionScanner(Configuration, ClientState, PlayerState, DataManager, Log, ItemSetRepository);
        mainWindow = new MainWindow(Configuration, ItemSetRepository, Scanner, PlayerState, DataManager, TextureProvider);

        windowSystem.AddWindow(mainWindow);
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open AkuItemSets and scan this character's item set collection.",
        });

        ClientState.Login += OnLogin;
        if (ClientState.IsLoggedIn)
        {
            Scanner.ScanCurrentCharacter();
        }
    }

    public void Dispose()
    {
        ClientState.Login -= OnLogin;
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        windowSystem.RemoveAllWindows();
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("scan", StringComparison.OrdinalIgnoreCase))
        {
            Scanner.ScanCurrentCharacter();
            mainWindow.IsOpen = true;
            return;
        }

        ToggleMainUi();
    }

    private void OnLogin() => Scanner.ScanCurrentCharacter();

    private void ToggleMainUi() => mainWindow.Toggle();
}
