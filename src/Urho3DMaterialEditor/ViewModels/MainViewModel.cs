﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json;
using Toe.Scripting;
using Toe.Scripting.Helpers;
using Toe.Scripting.WPF;
using Toe.Scripting.WPF.ViewModels;
using Toe.Scripting.WPF.Views;
using Urho;
using Urho3DMaterialEditor.Model;
using Urho3DMaterialEditor.Views;

namespace Urho3DMaterialEditor.ViewModels
{
    public class MainViewModel : ViewModelBase, IDisposable
    {
        private readonly ConfigurationRepository<AppConfiguration> _config;
        private readonly UrhoContext _context;
        private readonly ShaderGenerator _generator;
        private readonly ScriptingCommand _rearrangeCommand;

        private readonly SerialDisposable _testSubscription = new SerialDisposable();
        private readonly GraphValidator _validator;
        private PreviewApplication _application;
        private readonly EventLoopScheduler _backgroundScheduler;

        private PinViewModel _currentPreviewPin;


        private readonly CompositeDisposable _disposables = new CompositeDisposable();
        private readonly Subject<MaterialCompilationException> _errors;
        private string _fileName;
        private IObserver<MaterialCompilationException> _handleError;
        private SceneNodeViewModel _node;
        private IList<SceneNodeViewModel> _nodes;

        private readonly Subject<PreviewData> _preview;
        private string _status = "";
        private IObserver<INodeFactory> _testFactory;

        public MainViewModel(ScriptViewModel scriptViewModel, ShaderGenerator generator, UrhoContext context,
            GraphValidator validator, ConfigurationRepository<AppConfiguration> config)
        {
            _backgroundScheduler = new EventLoopScheduler();
            _disposables.Add(_backgroundScheduler);
            _generator = generator;
            _context = context;
            _validator = validator;
            _config = config;
            NewCommand = new ScriptingCommand(New);
            NewEmptyCommand = new ScriptingCommand(NewEmpty);
            OpenCommand = new ScriptingCommand(Open);
            MergeCommand = new ScriptingCommand(Merge);
            SaveCommand = new ScriptingCommand(Save);
            SaveAsCommand = new ScriptingCommand(SaveAs);
            OpenSceneCommand = new ScriptingCommand<string>(OpenScene);
            OpenMDLCommand = new ScriptingCommand<string>(OpenMDL);
            OpenAnimatedMDLCommand = new ScriptingCommand<string>(OpenAnimatedMDL);

            SetResourcePathCommand = new ScriptingCommand(SetResourcePath);
            ExitCommand = new ScriptingCommand(Exit);
            SetRenderPathCommand = new ScriptingCommand<string>(SetRenderPath);
            SetShadowQualityCommand = new ScriptingCommand<string>(SetShadowQuality);
            _rearrangeCommand = new ScriptingCommand(() => ScriptViewModel.Rearrange());
            SaveSelectedAsCommand = new ScriptingCommand(SaveSelectedAs);
            RunScriptAnalizerCommand = new ScriptingCommand(RunScriptAnalizer);
            GenDocumentationCommand = new ScriptingCommand(GenDocumentation);
            TestAllNodeTypesCommand = new ScriptingCommand(TestAllNodeTypes);
            TestNodesCommand = new ScriptingCommand(TestNodes);
            TestPerformanceCommand = new ScriptingCommand(TestPerformance);
            ScriptViewModel = scriptViewModel;

            _errors = new Subject<MaterialCompilationException>();
            _disposables.Add(_errors.ObserveOnDispatcher().Subscribe(HandleError));

            _preview = new Subject<PreviewData>();
            _disposables.Add(_preview.Throttle(TimeSpan.FromSeconds(0.1f)).ObserveOn(_backgroundScheduler)
                .Subscribe(new Generator(this)));

            ScriptViewModel.ScriptChanged += OnScriptViewModelOnScriptChanged;
            ScriptViewModel.SelectionChanged += OnScriptViewModelOnSelectionChanged;
            New();
        }

        public ScriptingCommand TestNodesCommand { get; set; }
        public ScriptingCommand TestPerformanceCommand { get; set; }


        public ScriptingCommand MergeCommand { get; set; }

        public ScriptingCommand SaveSelectedAsCommand { get; set; }


        public ScriptingCommand RunScriptAnalizerCommand { get; set; }
        public ScriptingCommand GenDocumentationCommand { get; set; }
        public ScriptingCommand TestAllNodeTypesCommand { get; set; }


        public ScriptingCommand<string> SetRenderPathCommand { get; set; }
        public ScriptingCommand<string> SetShadowQualityCommand { get; set; }

        public ICommand RearrangeCommand => _rearrangeCommand;

        public ScriptingCommand SaveAsCommand { get; set; }

        public ScriptingCommand SaveCommand { get; set; }

        public ScriptingCommand OpenCommand { get; set; }

        public ICommand OpenSceneCommand { get; set; }

        public ICommand OpenMDLCommand { get; set; }
        public ICommand OpenAnimatedMDLCommand { get; set; }

        public ScriptingCommand SetResourcePathCommand { get; set; }
        public ScriptingCommand ExitCommand { get; set; }

        public string FileName
        {
            get => _fileName;
            set
            {
                RaiseAndSetIfChanged(ref _fileName, value);
                SaveCommand.CanExecute = _fileName != null;
            }
        }

        public string Status
        {
            get => _status;
            set => RaiseAndSetIfChanged(ref _status, value ?? "");
        }

        public ICommand NewCommand { get; }
        public ICommand NewEmptyCommand { get; }


        public ScriptViewModel ScriptViewModel { get; }

        public PreviewApplication Application
        {
            get => _application;
            set
            {
                if (_application != value)
                {
                    _application = value;
                    _application.NodeListUpdated += (s, a) => UpdateNodes(a.Nodes);
                    _preview.OnNext(new PreviewData {Script = ScriptViewModel.Script?.Clone()});
                }
            }
        }

        public IList<SceneNodeViewModel> Nodes
        {
            get => _nodes;
            set => RaiseAndSetIfChanged(ref _nodes, value);
        }

        public SceneNodeViewModel SelectedNode
        {
            get => _node;
            set
            {
                if (RaiseAndSetIfChanged(ref _node, value)) _application.SelectNode(_node.Info);
            }
        }

        public void Dispose()
        {
            _disposables.Dispose();
        }

        private void TestPerformance()
        {
            var watch = new Stopwatch();
            watch.Start();
            for (var i = 0; i < 50; ++i) GenerateMaterial("_temp", ScriptViewModel.Script);
            watch.Stop();
            MessageBox.Show(watch.Elapsed.ToString(), "Time", MessageBoxButton.OK);
        }

        private void OnScriptViewModelOnScriptChanged(object s, ScriptChangedEventArgs a)
        {
            _preview.OnNext(new PreviewData {Script = ScriptViewModel.Script?.Clone()});
        }

        private void OnScriptViewModelOnSelectionChanged(object s, EventArgs a)
        {
            if (!GenerateTempMaterialForLink())
                if (_currentPreviewPin != null)
                {
                    _currentPreviewPin = null;
                    _preview.OnNext(new PreviewData {Script = ScriptViewModel.Script?.Clone()});
                }
        }

        private void TestNodes()
        {
            _testSubscription.Disposable =
                Observable
                    .Timer(DateTimeOffset.Now, TimeSpan.FromSeconds(2))
                    .Zip(ScriptViewModel.Registry.ToObservable(), (t, f) => f)
                    .ObserveOnDispatcher()
                    .Subscribe(TestFactory);
        }

        public void TestFactory(INodeFactory factory)
        {
            Trace.WriteLine(factory);
        }

        private void GenDocumentation()
        {
            var folder = @"C:\Temp\Docs";
            Directory.CreateDirectory(folder);
            foreach (var factory in ScriptViewModel.Registry)
            {
                var name = factory.Name;
            }
        }

        private void TestAllNodeTypes()
        {
            foreach (var factory in ScriptViewModel.Registry)
            {
                var name = factory.Name;
            }
        }

        public void Exit()
        {
            if (ScriptViewModel.HasUnsavedChanged)
            {
                var res = MessageBox.Show("You may have unsaved changes.\nDo you want to exit the application anyway?",
                    "Unsaved changes", MessageBoxButton.OKCancel, MessageBoxImage.Exclamation);
                if (res != MessageBoxResult.OK)
                    return;
            }

            Environment.Exit(0);
        }

        private void SetResourcePath()
        {
            var vm = new AppFoldersViewModel(_config);
            var dialog = new AppFoldersDialog(vm);
            if (dialog.ShowDialog() == true) vm.Apply();
        }

        private void RunScriptAnalizer()
        {
            try
            {
                var script = ScriptViewModel.Script;
                var s = new ScriptHelper<TranslatedMaterialGraph.NodeInfo>(script);
                TranslatedMaterialGraph.Preprocess(s);
                var scriptAnalyser = TranslatedMaterialGraph.Translate(s);
                new ScriptDialog(scriptAnalyser.Script.BuildScript()).ShowDialog();
            }
            catch (MaterialCompilationException exception)
            {
                Trace.WriteLine(exception);
            }
        }

        private void Merge()
        {
            var file = _context.PickFile("MaterialGraphs", "json");
            if (file != null)
            {
                FileName = file.Absolute;
                var script = JsonConvert.DeserializeObject<Script>(_context.ReadAllText(file.Absolute));
                ScriptViewModel.MergeWith(script);
            }
        }

        private void SetShadowQuality(string shadowQuality)
        {
            _application.SetShadowQuality((ShadowQuality) Enum.Parse(typeof(ShadowQuality), shadowQuality));
        }

        private void SetRenderPath(string renderPath)
        {
            _application.SetRenderPath("RenderPaths/" + renderPath + ".xml");
        }

        private void SaveAs()
        {
            var file = _context.PickSaveFile("MaterialGraphs", "json");
            if (file != null)
            {
                FileName = file.Absolute;
                _context.WriteAllText(FileName,
                    JsonConvert.SerializeObject(ScriptViewModel.Script, Formatting.Indented,
                        new JsonSerializerSettings {TypeNameHandling = TypeNameHandling.None}));
                GenerateMaterial(Path.GetFileNameWithoutExtension(file.Absolute), ScriptViewModel.Script)?.Save();
            }
        }

        private void SaveSelectedAs()
        {
            var file = _context.PickSaveFile("MaterialGraphs", "json");
            if (file != null)
                _context.WriteAllText(file.Absolute,
                    JsonConvert.SerializeObject(ScriptViewModel.ExtractSelected(), Formatting.Indented,
                        new JsonSerializerSettings {TypeNameHandling = TypeNameHandling.None}));
        }

        private void Save()
        {
            if (FileName == null)
            {
                SaveAs();
            }
            else
            {
                _context.WriteAllText(FileName,
                    JsonConvert.SerializeObject(ScriptViewModel.Script, Formatting.Indented,
                        new JsonSerializerSettings {TypeNameHandling = TypeNameHandling.None}));
                GenerateMaterial(Path.GetFileNameWithoutExtension(FileName), ScriptViewModel.Script)?.Save();
            }
        }

        private void Open()
        {
            var file = _context.PickFile("MaterialGraphs", "json");
            if (file != null)
            {
                FileName = file.Absolute;
                ScriptViewModel.Script = JsonConvert.DeserializeObject<Script>(_context.ReadAllText(file.Absolute),
                    new JsonSerializerSettings {MissingMemberHandling = MissingMemberHandling.Ignore});
                ScriptViewModel.ResetUndoStack();
                ScriptViewModel.HasUnsavedChanged = false;
            }
        }

        private void OpenScene(string sceneType)
        {
            switch (sceneType)
            {
                case "Point":
                    Application.CreateScene(LightType.Point);
                    return;
                case "Spot":
                    Application.CreateScene(LightType.Spot);
                    return;
                case "Directional":
                    Application.CreateScene(LightType.Directional);
                    return;
            }

            var file = _context.PickFile("Scenes", "xml");
            if (file != null) Application.OpenScene(file.Absolute);
        }

        private void OpenMDL(string mdl)
        {
            var file = _context.PickFile("Models", "mdl");
            if (file != null) Application.LoadModel(file.Relative);
        }

        private void OpenAnimatedMDL(string mdl)
        {
            var file = _context.PickFile("Models", "mdl");
            if (file != null)
            {
                var animation = _context.PickFile("Animations", "ani");
                Application.LoadAnimatedModel(file.Relative, animation.Relative);
            }
        }

        private void NewEmpty()
        {
            var script = new Script();
            ScriptViewModel.Script = script;

            ScriptViewModel.ResetUndoStack();
            ScriptViewModel.HasUnsavedChanged = false;
            FileName = null;
        }

        private void New()
        {
            var script = MaterialNodeRegistry.ReadScriptResource("Urho3DMaterialEditor.Data.NewMaterial.json");
            ScriptViewModel.Script = script;

            ScriptViewModel.ResetUndoStack();
            ScriptViewModel.HasUnsavedChanged = false;
            FileName = null;
        }

        private bool GenerateTempMaterialForLink()
        {
            if (ScriptViewModel.NumSelectedItems != 1) return false;
            var link = ScriptViewModel.GetSelectedItems().First() as LinkViewModel;
            if (link == null) return false;

            var pin = link.From as PinViewModel;
            if (pin == null) return false;

            switch (pin.Type)
            {
                case PinTypes.Float:
                case PinTypes.Vec2:
                case PinTypes.Vec3:
                case PinTypes.Vec4:
                    _currentPreviewPin = pin;
                    _preview.OnNext(new PreviewData
                    {
                        Script = ScriptViewModel.Script?.Clone(),
                        PreviewPin = new Connection(pin.Node.Id, pin.Id)
                    });
                    return true;
            }

            return false;
        }

        protected void HandleError(MaterialCompilationException ex)
        {
            CleanErrors();
            if (ex != null)
            {
                var failedNodes = new HashSet<int>(ex.Pins.Select(_ => _.NodeId));
                foreach (var item in ScriptViewModel.Nodes.Where(_ => failedNodes.Contains(_.Id)))
                    item.Error = ex.InnerException?.Message ?? ex.Message;
            }
        }

        internal PreivewContent GenerateMaterial(string name, Script script, Connection previewPin = null)
        {
            {
                var ex = _validator.Validate(ScriptViewModel);
                if (ex != null)
                {
                    _errors.OnNext(ex);
                    return null;
                }
            }

            if (script != null)
                try
                {
                    var res = _generator.Generate(script, name, previewPin);
                    _errors.OnNext(null);
                    return res;
                }
                catch (MaterialCompilationException ex)
                {
                    _errors.OnNext(ex);
                }

            return null;
        }

        private void CleanErrors()
        {
            foreach (var item in ScriptViewModel.Nodes) item.Error = null;
            foreach (var item in ScriptViewModel.Groups) item.ErrorNode = null;
            ScriptViewModel.ErrorNode = null;
        }

        private void UpdateNodes(List<PreviewApplication.NodeListUpdatedArgs.NodeInfo> nodes)
        {
            Nodes = nodes.Select(_ => new SceneNodeViewModel(_)).ToList();
        }

        internal class PreviewData
        {
            public Connection PreviewPin;
            public Script Script;
        }
    }
}