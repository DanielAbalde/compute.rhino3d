using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using Resthopper.IO;
using Newtonsoft.Json;
using Rhino.Geometry;
using Compute.Components;

namespace Compute.Components
{ 
    /// <summary>
    /// Base class for remote components
    /// </summary>
    public abstract class RemoteComponent : GH_TaskCapableComponent<Schema>, IGH_VariableParameterComponent
    {
        #region Fields 
        protected RemoteDefinition _remoteDefinition = null;
        protected bool _cacheResultsInMemory = true;
        protected bool _cacheResultsOnServer = true;
        protected bool _remoteDefinitionRequiresRebuild = false;
        protected static bool _isHeadless = false;
        protected GH_IO.Types.GH_Version _version;
        protected const string TagVersion = "RemoteSolveVersion";
        protected const string TagPath = "RemoteDefinitionLocation";
        protected const string TagCacheResultsOnServer = "CacheSolveResults";
        protected const string TagCacheResultsInMemory = "CacheResultsInMemory";
        #endregion

        #region Properties
        public override Guid ComponentGuid => GetType().GUID;
        public override GH_Exposure Exposure => GH_Exposure.tertiary; 
        // keep public in case external C# code wants to set this
        public string RemoteDefinitionLocation
        {
            get
            {
                if (_remoteDefinition != null)
                {
                    return _remoteDefinition.Path;
                }
                return string.Empty;
            }
            set
            {
                if (!string.Equals(RemoteDefinitionLocation, value, StringComparison.OrdinalIgnoreCase))
                {
                    if (_remoteDefinition != null)
                    {
                        _remoteDefinition.Dispose();
                        _remoteDefinition = null;
                    }
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        _remoteDefinition = RemoteDefinition.Create(value, this);
                        DefineInputsAndOutputs();
                    }
                }
            }
        }

        #endregion

        #region Constructors
        protected RemoteComponent(string name, string nickname, string description,
            string category, string subcategory, int mayorVersion, int minorVersion, int revisionVersion)
            : base(name, nickname, description, category, subcategory)
        {
            _version = new GH_IO.Types.GH_Version(mayorVersion, minorVersion, revisionVersion);
            _isHeadless = Rhino.RhinoApp.IsRunningHeadless;
        }
        #endregion

        #region Methods
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            if (string.IsNullOrWhiteSpace(RemoteDefinitionLocation))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No URL or path defined for definition");
                return;
            }

            // Don't allow hops components to run on compute for now.
            if (_isHeadless)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Hops components are not allowed to run in external definitions. Please help us understand why you need this by emailing steve@mcneel.com");
                return;
            }

            if (InPreSolve)
            { 
                var inputSchema = _remoteDefinition.CreateSolveInput(DA, _cacheResultsOnServer, out List<string> warnings);
                if (warnings != null && warnings.Count > 0)
                {
                    foreach (var warning in warnings)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, warning);
                    }
                    return;
                }
                if (inputSchema != null)
                {
                    var task = System.Threading.Tasks.Task.Run(() => _remoteDefinition.Solve(inputSchema, _cacheResultsInMemory));
                    TaskList.Add(task);
                }
                return;
            }

            if (!GetSolveResults(DA, out var schema))
            { 
                var inputSchema = _remoteDefinition.CreateSolveInput(DA, _cacheResultsOnServer, out List<string> warnings);
                if (warnings != null && warnings.Count > 0)
                {
                    foreach (var warning in warnings)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, warning);
                    }
                    return;
                }
                if (inputSchema != null)
                    schema = _remoteDefinition.Solve(inputSchema, _cacheResultsInMemory);
                else
                    schema = null;
            }

            if (DA.Iteration == 0)
            {
                // TODO: Having to clear the output data seems like a bug in the
                // TaskCapable components logic. We need to investigate this further.
                foreach (var output in Params.Output)
                    output.ClearData();
            }

            if (schema != null)
            {
                _remoteDefinition.SetComponentOutputs(schema, DA, Params.Output, this);
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
        }

        #endregion

        #region Serialization
        public override bool Write(GH_IWriter writer)
        {
            bool rc = base.Write(writer);
            if (rc)
            {
                writer.SetVersion(TagVersion, _version.major, _version.minor, _version.revision);
                writer.SetString(TagPath, RemoteDefinitionLocation);
                writer.SetBoolean(TagCacheResultsOnServer, _cacheResultsOnServer);
                writer.SetBoolean(TagCacheResultsInMemory, _cacheResultsInMemory);
            }
            return rc;
        }
        public override bool Read(GH_IReader reader)
        {
            bool rc = base.Read(reader);
            if (rc)
            {
                _version = reader.GetVersion(TagVersion); 
                string path = reader.GetString(TagPath);
                try
                {
                    RemoteDefinitionLocation = path;
                }
                catch (System.Net.WebException)
                {
                    // this can happen if a server is not responding and is acceptable in this
                    // case as we want to read without throwing exceptions
                }

                bool cacheResults = _cacheResultsOnServer;
                if (reader.TryGetBoolean(TagCacheResultsOnServer, ref cacheResults))
                    _cacheResultsOnServer = cacheResults;

                cacheResults = _cacheResultsInMemory;
                if (reader.TryGetBoolean(TagCacheResultsInMemory, ref cacheResults))
                    _cacheResultsInMemory = cacheResults;
            }
            return rc;
        }
        #endregion

        #region IGH_VariableParameterComponent
        public bool CanInsertParameter(GH_ParameterSide side, int index) => false;
        public bool CanRemoveParameter(GH_ParameterSide side, int index) => false;
        public IGH_Param CreateParameter(GH_ParameterSide side, int index) => null;
        public bool DestroyParameter(GH_ParameterSide side, int index) => true;
        public void VariableParameterMaintenance() { }
        #endregion

        #region Menu 
        protected void AppendMenuLocalCompute(ToolStripDropDown menu)
        {
            var tsi = new ToolStripMenuItem($"Local Computes ({Servers.ActiveLocalComputeCount})");
            var tsi_sub = new ToolStripMenuItem("1 More", null, (s, e) => {
                Servers.LaunchLocalCompute(false);
            });
            tsi_sub.ToolTipText = "Launch a local compute instance";
            tsi.DropDown.Items.Add(tsi_sub);
            tsi_sub = new ToolStripMenuItem("6 Pack", null, (s, e) => {
                for (int i = 0; i < 6; i++)
                    Servers.LaunchLocalCompute(false);
            });
            tsi_sub.ToolTipText = "Get drunk with power and launch 6 compute instances";
            tsi.DropDown.Items.Add(tsi_sub);
            menu.Items.Add(tsi);
        }
        protected void AppendMenuCacheInMemory(ToolStripDropDown menu)
        {
            var tsi = new ToolStripMenuItem("Cache In Memory", null, (s, e) => { _cacheResultsInMemory = !_cacheResultsInMemory; });
            tsi.ToolTipText = "Keep previous results in memory cache";
            tsi.Checked = _cacheResultsInMemory;
            menu.Items.Add(tsi);
        }
        protected void AppendMenuCacheInServer(ToolStripDropDown menu)
        {
            var tsi = new ToolStripMenuItem("Cache On Server", null, (s, e) => { _cacheResultsOnServer = !_cacheResultsOnServer; });
            tsi.ToolTipText = "Tell the compute server to cache results for reuse in the future";
            tsi.Checked = _cacheResultsOnServer;
            menu.Items.Add(tsi);
        }
        #endregion

        #region Remote
        protected abstract void DefineInputsAndOutputs();

        public void OnRemoteDefinitionChanged()
        {
            if (_remoteDefinitionRequiresRebuild)
                return;

            // this is typically called on a different thread than the main UI thread
            _remoteDefinitionRequiresRebuild = true;
            Rhino.RhinoApp.Idle += RhinoApp_Idle;
        }

        private void RhinoApp_Idle(object sender, EventArgs e)
        {
            if (!_remoteDefinitionRequiresRebuild)
            {
                // not sure how this could happen, but in case it does just
                // remove the idle event and bail
                Rhino.RhinoApp.Idle -= RhinoApp_Idle;
                return;
            }

            var ghdoc = OnPingDocument();
            if (ghdoc != null && ghdoc.SolutionState == GH_ProcessStep.Process)
            {
                // Processing a solution. Wait until the next idle event to do something
                return;
            }

            // stop the idle event watcher
            Rhino.RhinoApp.Idle -= RhinoApp_Idle;
            _remoteDefinitionRequiresRebuild = false;
            DefineInputsAndOutputs();
        }
        #endregion
    }
}
