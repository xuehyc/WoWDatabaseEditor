﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using AsyncAwaitBestPractices.MVVM;
using Prism.Commands;
using Prism.Events;
using ReactiveUI;
using WDE.Common.Events;
using WDE.Common.Managers;
using WDE.Common.Services.MessageBox;
using WDE.Common.Windows;
using WDE.Common.Menu;
using WDE.Common.Providers;
using WDE.Common.QuickAccess;
using WDE.Common.Services;
using WDE.Common.Sessions;
using WDE.Common.Solution;
using WDE.Common.Tasks;
using WDE.Common.Utils;
using WDE.Module.Attributes;
using WDE.MVVM;
using WDE.MVVM.Observable;
using WoWDatabaseEditor.Providers;
using WoWDatabaseEditorCore.Managers;
using WoWDatabaseEditorCore.Services;
using WoWDatabaseEditorCore.Services.FindAnywhere;
using WoWDatabaseEditorCore.Services.Profiles;
using WoWDatabaseEditorCore.Services.QuickAccess;

namespace WoWDatabaseEditorCore.ViewModels
{
    [SingleInstance]
    [AutoRegister]
    public class MainWindowViewModel : ObservableBase, ILayoutViewModelResolver, ICloseAwareViewModel
    {
        private readonly IMessageBoxService messageBoxService;
        private readonly Func<AboutViewModel> aboutViewModelCreator;
        private readonly Func<QuickStartViewModel> quickStartCreator;
        private readonly Func<TextDocumentViewModel> textDocumentCreator;
        private readonly ISolutionTasksService solutionTasksService;
        private readonly IProgramNameService programNameService;
        private readonly List<IProgramNameAddon> programNameAddons;
        private readonly ITablesToolService tablesToolService;
        private readonly IGlobalServiceRoot globalServiceRoot;

        private readonly Dictionary<string, ITool> toolById = new();

        public MainWindowViewModel(IDocumentManager documentManager,
            IStatusBar statusBar,
            IMessageBoxService messageBoxService,
            TasksViewModel tasksViewModel,
            EditorMainMenuItemsProvider menuItemProvider,
            RelatedSolutionItems relatedSolutionItems,
            ISolutionSqlService solutionSqlService,
            Func<AboutViewModel> aboutViewModelCreator,
            Func<QuickStartViewModel> quickStartCreator,
            Func<TextDocumentViewModel> textDocumentCreator,
            ISolutionTasksService solutionTasksService,
            ISolutionItemSqlGeneratorRegistry queryGeneratorRegistry,
            IClipboardService clipboardService,
            ISessionService sessionService,
            ITaskRunner taskRunner,
            IEventAggregator eventAggregator,
            IProgramNameService programNameService,
            IEnumerable<IProgramNameAddon> nameAddons,
            IMainThread mainThread,
            IQuickAccessViewModel quickAccessViewModel,
            IWindowManager windowManager,
            ITablesToolService tablesToolService,
            Lazy<IGameViewService> gameViewService,
            QuickGoToViewModel quickGoToViewModel,
            ProfilesViewModel profilesViewModel,
            IGlobalServiceRoot globalServiceRoot,
            Func<IFindAnywhereDialogViewModel> findAnywhereCreator)
        {
            DocumentManager = documentManager;
            StatusBar = statusBar;
            this.messageBoxService = messageBoxService;
            this.aboutViewModelCreator = aboutViewModelCreator;
            this.quickStartCreator = quickStartCreator;
            this.textDocumentCreator = textDocumentCreator;
            this.solutionTasksService = solutionTasksService;
            this.programNameService = programNameService;
            this.tablesToolService = tablesToolService;
            this.globalServiceRoot = globalServiceRoot;
            this.programNameAddons = nameAddons.ToList();
            Title = "";
            Subtitle = programNameService.Subtitle;
            foreach (var titleAddon in programNameAddons)
                titleAddon.ToObservable(x => x.Addon).SubscribeAction(_ => UpdateTitle());
            UpdateTitle();
            OpenDocument = new DelegateCommand<IMenuDocumentItem>(ShowDocument);
            ExecuteChangedCommand = new DelegateCommand(() =>
            {
                var item = DocumentManager.ActiveSolutionItemDocument?.SolutionItem;
                if (item == null)
                {
                    if (DocumentManager.ActiveDocument is { } doc)
                        doc.Save.Execute(null);
                    return;
                }

                solutionTasksService.Save(DocumentManager.ActiveSolutionItemDocument!);
            }, () => (DocumentManager.ActiveSolutionItemDocument != null &&
                     (solutionTasksService.CanSaveAndReloadRemotely || solutionTasksService.CanSaveToDatabase)) ||
                     (DocumentManager.ActiveDocument is {} doc && doc.Save.CanExecute(null)));

            CopyCurrentSqlCommand = new AsyncAutoCommand(async () =>
            {
                if (DocumentManager.ActiveDocument is ISolutionItemDocument { SolutionItem: { } } sid)
                {
                    await taskRunner.ScheduleTask("Generating SQL",
                        async () =>
                        {
                            var sql = await queryGeneratorRegistry.GenerateSql(sid.SolutionItem);
                            clipboardService.SetText(sql.QueryString);
                            statusBar.PublishNotification(new PlainNotification(NotificationType.Success, "SQL copied!"));
                        });
                }
            }, () => DocumentManager.ActiveDocument != null && DocumentManager.ActiveDocument is ISolutionItemDocument);
            
            GenerateCurrentSqlCommand = new DelegateCommand(() =>
            {
                if (DocumentManager.ActiveDocument is ISolutionItemDocument {SolutionItem: { }} sid)
                    solutionSqlService.OpenDocumentWithSqlFor(sid.SolutionItem);
            }, () => DocumentManager.ActiveDocument != null && DocumentManager.ActiveDocument is ISolutionItemDocument);

            FindAnywhereCommand = new AsyncAutoCommand(async () =>
            {
                await windowManager.ShowDialog(findAnywhereCreator());
            });

            Open3DCommand = new DelegateCommand(() =>
            {
                gameViewService.Value.Open();
            });
            
            DocumentManager.ToObservable(dm => dm.ActiveDocument)
                .SubscribeAction(_ =>
                {
                    GenerateCurrentSqlCommand.RaiseCanExecuteChanged();
                    ExecuteChangedCommand.RaiseCanExecuteChanged();
                    CopyCurrentSqlCommand.RaiseCanExecuteChanged();
                });
            
            TasksViewModel = tasksViewModel;
            RelatedSolutionItems = relatedSolutionItems;
            QuickAccessViewModel = quickAccessViewModel;
            QuickGoToViewModel = quickGoToViewModel;
            ProfilesViewModel = profilesViewModel;

            MenuItemProviders = menuItemProvider.GetItems();

            foreach (var window in documentManager.AllTools)
                toolById[window.UniqueId] = window;

            documentManager.OpenedDocuments.ToCountChangedObservable().SubscribeAction(count =>
            {
                if (count == 0)
                    mainThread.Dispatch(ShowStartPage);
            });
            //LoadDefault();
            
            Watch(DocumentManager, dm => dm.ActiveSolutionItemDocument, nameof(ShowExportButtons));
            Watch(DocumentManager, dm => dm.ActiveDocument, nameof(ShowPlayButtons));
            Watch(tablesToolService, serv => serv.Visibility, nameof(ShowTablesList));
            
            eventAggregator.GetEvent<AllModulesLoaded>()
                .Subscribe(OpenFatalLogIfExists, ThreadOption.PublisherThread, true);
        }

        private void OpenFatalLogIfExists()
        {
            if (!FatalErrorHandler.HasFatalLog())
                return;

            var log = FatalErrorHandler.ConsumeFatalLog();
            DocumentManager.OpenDocument(textDocumentCreator().Set("Crash log", log));
            
            messageBoxService.ShowDialog(new MessageBoxFactory<bool>()
                .SetTitle("WoW Database Editor has been closed due to the fatal error")
                .SetIcon(MessageBoxIcon.Error)
                .SetMainInstruction("WoW Database Editor has been closed due to the fatal error")
                .SetContent("Sorry, the editor has been closed to the fatal error, a log with the error is now opened, you can report the bug via Help -> Report a bug and attach the log")
                .WithOkButton(true)
                .Build());
        }

        private void UpdateTitle()
        {
            if (programNameAddons.Count == 0)
                Title = programNameService.Title;
            else
            {
                var name = string.Join(" ", programNameAddons.Select(a => a.Addon));
                Title = programNameService.Title + " " + name;
            }
            RaisePropertyChanged(nameof(Title));
        }

        public IStatusBar StatusBar { get; }
        public IDocumentManager DocumentManager { get; }

        public TasksViewModel TasksViewModel { get; }
        public RelatedSolutionItems RelatedSolutionItems { get; }
        public IQuickAccessViewModel QuickAccessViewModel { get; }
        public QuickGoToViewModel QuickGoToViewModel { get; }
        public ProfilesViewModel ProfilesViewModel { get; }

        public List<IMainMenuItem> MenuItemProviders { get; }

        public string Title { get; private set; }
        
        public string Subtitle { get; }

        public DelegateCommand<IMenuDocumentItem> OpenDocument { get; }

        // this fallback to QuickStartViewModel is a hack, otherwise Avalonia for some reason will never show this button if it is not visible in the beginning
        public bool ShowPlayButtons => (DocumentManager.ActiveSolutionItemDocument?.ShowExportToolbarButtons ?? false) || (DocumentManager.ActiveDocument?.Save?.CanExecute(null) ?? false);
        
        public bool ShowExportButtons => DocumentManager.ActiveSolutionItemDocument?.ShowExportToolbarButtons ?? true;
        
        public DelegateCommand ExecuteChangedCommand { get; }
        
        public AsyncAutoCommand CopyCurrentSqlCommand { get; }
        
        public DelegateCommand GenerateCurrentSqlCommand { get; }

        public ICommand FindAnywhereCommand { get; }
        
        public ICommand Open3DCommand { get; }
        
        public bool ShowTablesList
        {
            get => tablesToolService.Visibility;
            set
            {
                if (value)
                    tablesToolService.Open();
                else
                    tablesToolService.Close();
            }
        }
        
        private void ShowAbout()
        {
            DocumentManager.OpenDocument(aboutViewModelCreator());
        }

        private void ShowStartPage()
        {
            DocumentManager.OpenDocument(quickStartCreator());
        }
        
        private void ShowDocument(IMenuDocumentItem documentItem)
        {
            DocumentManager.OpenDocument(documentItem.EditorDocument());
        }

        public ITool? ResolveViewModel(string id)
        {
            if (toolById.TryGetValue(id, out var tool))
            {
                DocumentManager.OpenedTools.Add(tool);
                return tool;
            }

            return null;
        }

        public void LoadDefault()
        {
            foreach (var tool in toolById.Values)
            {
                if (tool.OpenOnStart)
                    DocumentManager.OpenTool(tool.GetType());
            }
        }

        private bool inCanClose;
        public async Task<bool> CanClose()
        {
            if (inCanClose)
            {
                await messageBoxService.SimpleDialog("Closing in progress", "Closing in progress",
                    "This app is already being closing (probably some async operation going in background)");
                return false;
            }

            inCanClose = true;

            try
            {
                var modifiedDocuments = DocumentManager.OpenedDocuments.Where(d => d.IsModified).ToList();

                if (modifiedDocuments.Count > 0)
                {
                    while (modifiedDocuments.Count > 0)
                    {
                        var editor = modifiedDocuments[^1];
                        var message = new MessageBoxFactory<MessageBoxButtonType>().SetTitle("Document is modified")
                            .SetMainInstruction("Do you want to save the changes of " + editor.Title + "?")
                            .SetContent("Your changes will be lost if you don't save them.")
                            .SetIcon(MessageBoxIcon.Warning)
                            .WithYesButton(MessageBoxButtonType.Yes)
                            .WithNoButton(MessageBoxButtonType.No)
                            .WithCancelButton(MessageBoxButtonType.Cancel);

                        if (modifiedDocuments.Count > 1)
                        {
                            message.SetExpandedInformation("Other modified documents:\n" +
                                                           string.Join("\n",
                                                               modifiedDocuments.SkipLast(1).Select(d => d.Title)));
                            message.WithButton("Yes to all", MessageBoxButtonType.CustomA)
                                .WithButton("No to all", MessageBoxButtonType.CustomB);
                        }

                        MessageBoxButtonType result = await messageBoxService.ShowDialog(message.Build());

                        if (result == MessageBoxButtonType.Cancel)
                            return false;

                        if (result == MessageBoxButtonType.Yes)
                        {
                            if (editor is IBeforeSaveConfirmDocument before)
                            {
                                if (await before.ShallSavePreventClosing())
                                    return false;
                            }
                            //editor.Save.Execute(null);
                            if (editor is ISolutionItemDocument solutionItemDocument)
                                await solutionTasksService.Save(solutionItemDocument);
                            else
                            {
                                if (editor.Save is IAsyncCommand async)
                                    await async.ExecuteAsync();
                                else
                                    editor.Save.Execute(null);
                            }
                            modifiedDocuments.RemoveAt(modifiedDocuments.Count - 1);
                            DocumentManager.OpenedDocuments.Remove(editor);
                        }
                        else if (result == MessageBoxButtonType.No)
                        {
                            modifiedDocuments.RemoveAt(modifiedDocuments.Count - 1);
                            DocumentManager.OpenedDocuments.Remove(editor);
                        }
                        else if (result == MessageBoxButtonType.CustomA)
                        {
                            foreach (var m in modifiedDocuments)
                            {
                                if (m is IBeforeSaveConfirmDocument before)
                                {
                                    if (await before.ShallSavePreventClosing())
                                    {
                                        return false;
                                    }
                                }
                                if (m is ISolutionItemDocument solutionItemDocument)
                                    await solutionTasksService.Save(solutionItemDocument);
                                else
                                    m.Save.Execute(null);
                            }
                            modifiedDocuments.Clear();
                        }
                        else if (result == MessageBoxButtonType.CustomB)
                        {
                            modifiedDocuments.Clear();
                        }
                    }
                }
                
                while (DocumentManager.OpenedDocuments.Count > 0)
                    DocumentManager.OpenedDocuments.RemoveAt(DocumentManager.OpenedDocuments.Count - 1);
                
                var modifiedTools = DocumentManager.AllTools
                    .Select(t => t as ISavableTool)
                    .Where(t => t != null)
                    .Cast<ISavableTool>()
                    .Where(t => t.IsModified)
                    .ToList();

                foreach (var tool in modifiedTools)
                {
                    var message = new MessageBoxFactory<MessageBoxButtonType>().SetTitle("Tool is modified")
                        .SetMainInstruction("Do you want to save the changes of " + tool.Title + "?")
                        .SetContent("Your changes will be lost if you don't save them.")
                        .SetIcon(MessageBoxIcon.Warning)
                        .WithYesButton(MessageBoxButtonType.Yes)
                        .WithNoButton(MessageBoxButtonType.No)
                        .WithCancelButton(MessageBoxButtonType.Cancel);

                    MessageBoxButtonType result = await messageBoxService.ShowDialog(message.Build());
                    if (result == MessageBoxButtonType.Cancel)
                        return false;

                    if (result == MessageBoxButtonType.Yes)
                    {
                        await SaveWithTimeout(tool);
                    }
                    else if (result == MessageBoxButtonType.No)
                    {
                    }
                }
                
                return true;
            }
            finally
            {
                inCanClose = false;
            }
        }

        private async Task SaveWithTimeout(ISavableTool tool)
        {
            var delay = Task.Delay(10000);

            var finishedTask = await Task.WhenAny(delay, tool.Save.ExecuteAsync());

            if (finishedTask == delay)
            {
                if (await messageBoxService.ShowDialog(new MessageBoxFactory<bool>()
                        .SetTitle("Error while saving")
                        .SetMainInstruction("Couldn't save " + tool.Title)
                        .SetContent(
                            "The save operation timed out. It might be fatal error or just connection problems. Do you want to try again?")
                        .WithYesButton(true)
                        .WithNoButton(false)
                        .Build()))
                    await SaveWithTimeout(tool);
            }
        }

        public async Task<bool> TryClose()
        {
            if (!await CanClose())
                return false;
            
            CloseRequest?.Invoke();
            return true;
        }

        public void ForceClose()
        {
            ForceCloseRequest?.Invoke();
        }

        public event Action CloseRequest = delegate{};
        public event Action ForceCloseRequest = delegate{};

        public void NotifyWillClose()
        {
            globalServiceRoot.Dispose();
        }
    }
}