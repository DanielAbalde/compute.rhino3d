﻿using System;
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
using GH_IO.Types;

namespace Compute.Components
{
    [Guid("C69BB52C-88BA-4640-B69F-188D111029E8")]
    public class HopsComponent : RemoteComponent
    {
 
        static HopsComponent()
        {
            if (!Rhino.Runtime.HostUtils.RunningOnWindows)
                return;
            if (Rhino.RhinoApp.IsRunningHeadless)
                return;
            if (Hops.HopsAppSettings.Servers.Length > 0)
                return;
            if (Hops.HopsAppSettings.LaunchWorkerAtStart)
            {
                Servers.StartServerOnLaunch();
            }
        }

        public HopsComponent()
          : base("Hops", "Hops", "Solve an external definition using Rhino Compute", "Params", "Util",0,2,0)
        { 
        }
         
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                return Hops24Icon();
            }
        }

        static System.Drawing.Bitmap _hops24Icon;
        static System.Drawing.Bitmap _hops48Icon;
        static System.Drawing.Bitmap Hops24Icon()
        {
            if (_hops24Icon == null)
            {
                var stream = typeof(HopsComponent).Assembly.GetManifestResourceStream("Hops.resources.Hops_24x24.png");
                _hops24Icon = new System.Drawing.Bitmap(stream);
            }
            return _hops24Icon;
        }
        public static System.Drawing.Bitmap Hops48Icon()
        {
            if (_hops48Icon == null)
            {
                var stream = typeof(HopsComponent).Assembly.GetManifestResourceStream("Hops.resources.Hops_48x48.png");
                _hops48Icon = new System.Drawing.Bitmap(stream);
            }
            return _hops48Icon;
        }

        public override void AppendAdditionalMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalMenuItems(menu);

            var tsi = new ToolStripMenuItem("&Path...", null, (sender, e) => { ShowSetDefinitionUi(); });
            tsi.Font = new System.Drawing.Font(tsi.Font, System.Drawing.FontStyle.Bold);
            menu.Items.Add(tsi);

            AppendMenuLocalCompute(menu);
            AppendMenuCacheInMemory(menu);
            AppendMenuCacheInServer(menu); 
        }

        /// <summary>
        /// Used for supporting double click on the component. 
        /// </summary>
        class ComponentAttributes : GH_ComponentAttributes
        {
            HopsComponent _component;
            public ComponentAttributes(HopsComponent parentComponent) : base(parentComponent)
            {
                _component = parentComponent;
            }

            protected override void Render(GH_Canvas canvas, System.Drawing.Graphics graphics, GH_CanvasChannel channel)
            {
                base.Render(canvas, graphics, channel);
                if (channel == GH_CanvasChannel.Objects &&
                    GH_Canvas.ZoomFadeMedium > 0 &&
                    !string.IsNullOrWhiteSpace(_component.RemoteDefinitionLocation)
                    )
                {
                    RenderHop(graphics, GH_Canvas.ZoomFadeMedium, new System.Drawing.PointF(Bounds.Right, Bounds.Bottom));
                }
            }

            void RenderHop(System.Drawing.Graphics graphics, int alpha, System.Drawing.PointF anchor)
            {
                var boxHops = new System.Drawing.RectangleF(anchor.X - 16, anchor.Y - 8, 16, 16);
                var bmp = HopsComponent.Hops48Icon();
                graphics.DrawImage(bmp, boxHops);
            }

            public override GH_ObjectResponse RespondToMouseDoubleClick(GH_Canvas sender, GH_CanvasMouseEvent e)
            {
                try
                {
                    _component.ShowSetDefinitionUi();
                }
                catch(Exception ex)
                {
                    _component.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                }
                return base.RespondToMouseDoubleClick(sender, e);
            }
        }

        public override void CreateAttributes()
        {
            Attributes = new ComponentAttributes(this);
        }

        void ShowSetDefinitionUi()
        {
            var form = new SetDefinitionForm(RemoteDefinitionLocation);
            if(form.ShowModal(Grasshopper.Instances.EtoDocumentEditor))
            {
                var comp = Grasshopper.Instances.ComponentServer.FindObjectByName(form.Path, true, true);
                if (comp != null)
                    RemoteDefinitionLocation = comp.Guid.ToString();
                else
                    RemoteDefinitionLocation = form.Path;
            }
        }
          
        protected override void DefineInputsAndOutputs()
        {
            ClearRuntimeMessages();
            string description = _remoteDefinition.GetDescription(out System.Drawing.Bitmap customIcon);
            if (!string.IsNullOrWhiteSpace(description) && !Description.Equals(description))
            {
                Description = description;
            }
            var inputs = _remoteDefinition.GetInputParams();
            var outputs = _remoteDefinition.GetOutputParams();

            bool buildInputs = inputs != null;
            bool buildOutputs = outputs != null;
            // check to see if the existing params match
            if (buildInputs && Params.Input.Count == inputs.Count)
            {
                buildInputs = false;
                foreach (var param in Params.Input.ToArray())
                {
                    if (!inputs.ContainsKey(param.Name))
                    {
                        buildInputs = true;
                        break;
                    }
                    else
                    {
                        // if input param exists, make sure param access is correct
                        var (input, _) = inputs[param.Name];
                        bool itemAccess = input.AtLeast == 1 && input.AtMost == 1;
                        param.Access = itemAccess ? GH_ParamAccess.item : GH_ParamAccess.list;
                    }
                }
            }
            if (buildOutputs && Params.Output.Count == outputs.Count)
            {
                buildOutputs = false;
                foreach (var param in Params.Output.ToArray())
                {
                    if (!outputs.ContainsKey(param.Name))
                    {
                        buildOutputs = true;
                        break;
                    }
                }
            }

            // Remove all existing inputs and outputs
            if (buildInputs)
            {
                foreach (var param in Params.Input.ToArray())
                {
                    Params.UnregisterInputParameter(param);
                }
            }
            if (buildOutputs)
            {
                foreach (var param in Params.Output.ToArray())
                {
                    Params.UnregisterOutputParameter(param);
                }
            }

            bool recompute = false;
            if (buildInputs && inputs != null)
            {
                bool containsEmptyDefaults = false;
                var mgr = CreateInputManager();
                foreach (var kv in inputs)
                {
                    string name = kv.Key;
                    var (input, param) = kv.Value;
                    GH_ParamAccess access = GH_ParamAccess.list;
                    if (input.AtLeast == 1 && input.AtMost == 1)
                        access = GH_ParamAccess.item;
                    string inputDescription = name;
                    if (!string.IsNullOrWhiteSpace(input.Description))
                        inputDescription = input.Description;
                    if (input.Default == null)
                        containsEmptyDefaults = true;
                    switch (param)
                    {
                        case Grasshopper.Kernel.Parameters.Param_Arc _:
                            mgr.AddArcParameter(name, name, inputDescription, access);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Boolean _:
                            if (input.Default == null)
                                mgr.AddBooleanParameter(name, name, inputDescription, access);
                            else
                                mgr.AddBooleanParameter(name, name, inputDescription, access, Convert.ToBoolean(input.Default));
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Box _:
                            mgr.AddBoxParameter(name, name, inputDescription, access);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Brep _:
                            mgr.AddBrepParameter(name, name, inputDescription, access);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Circle _:
                            mgr.AddCircleParameter(name, name, inputDescription, access);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Colour _:
                            mgr.AddColourParameter(name, name, inputDescription, access);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Complex _:
                            mgr.AddComplexNumberParameter(name, name, inputDescription, access);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Culture _:
                            mgr.AddCultureParameter(name, name, inputDescription, access);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Curve _:
                            mgr.AddCurveParameter(name, name, inputDescription, access);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Field _:
                            mgr.AddFieldParameter(name, name, inputDescription, access);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_FilePath _:
                            if (input.Default == null)
                                mgr.AddTextParameter(name, name, inputDescription, access);
                            else
                                mgr.AddTextParameter(name, name, inputDescription, access, input.Default.ToString());
                            break;
                        case Grasshopper.Kernel.Parameters.Param_GenericObject _:
                            throw new Exception("generic param not supported");
                        case Grasshopper.Kernel.Parameters.Param_Geometry _:
                            mgr.AddGeometryParameter(name, name, inputDescription, access);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Group _:
                            throw new Exception("group param not supported");
                        case Grasshopper.Kernel.Parameters.Param_Guid _:
                            throw new Exception("guid param not supported");
                        case Grasshopper.Kernel.Parameters.Param_Integer _:
                            if (input.Default == null)
                                mgr.AddIntegerParameter(name, name, inputDescription, access);
                            else
                                mgr.AddIntegerParameter(name, name, inputDescription, access, Convert.ToInt32(input.Default));
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Interval _:
                            mgr.AddIntervalParameter(name, name, inputDescription, access);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Interval2D _:
                            mgr.AddInterval2DParameter(name, name, inputDescription, access);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_LatLonLocation _:
                            throw new Exception("latlonlocation param not supported");
                        case Grasshopper.Kernel.Parameters.Param_Line _:
                            if (input.Default == null)
                                mgr.AddLineParameter(name, name, inputDescription, access);
                            else
                                mgr.AddLineParameter(name, name, inputDescription, access, JsonConvert.DeserializeObject<Line>(input.Default.ToString()));
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Matrix _:
                            mgr.AddMatrixParameter(name, name, inputDescription, access);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Mesh _:
                            mgr.AddMeshParameter(name, name, inputDescription, access);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_MeshFace _:
                            mgr.AddMeshFaceParameter(name, name, inputDescription, access);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_MeshParameters _:
                            throw new Exception("meshparameters paran not supported");
                        case Grasshopper.Kernel.Parameters.Param_Number _:
                            if (input.Default == null)
                                mgr.AddNumberParameter(name, name, inputDescription, access);
                            else
                                mgr.AddNumberParameter(name, name, inputDescription, access, Convert.ToDouble(input.Default));
                            break;
                        //case Grasshopper.Kernel.Parameters.Param_OGLShader:
                        case Grasshopper.Kernel.Parameters.Param_Plane _:
                            if (input.Default == null)
                                mgr.AddPlaneParameter(name, name, inputDescription, access);
                            else
                                mgr.AddPlaneParameter(name, name, inputDescription, access, JsonConvert.DeserializeObject<Plane>(input.Default.ToString()));
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Point _:
                            if (input.Default == null)
                                mgr.AddPointParameter(name, name, inputDescription, access);
                            else
                                mgr.AddPointParameter(name, name, inputDescription, access, JsonConvert.DeserializeObject<Point3d>(input.Default.ToString()));
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Rectangle _:
                            mgr.AddRectangleParameter(name, name, inputDescription, access);
                            break;
                        //case Grasshopper.Kernel.Parameters.Param_ScriptVariable _:
                        case Grasshopper.Kernel.Parameters.Param_String _:
                            if (input.Default == null)
                                mgr.AddTextParameter(name, name, inputDescription, access);
                            else
                                mgr.AddTextParameter(name, name, inputDescription, access, input.Default.ToString());
                            break;
                        case Grasshopper.Kernel.Parameters.Param_StructurePath _:
                            mgr.AddPathParameter(name, name, inputDescription, access);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_SubD _:
                            mgr.AddSubDParameter(name, name, inputDescription, access);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Surface _:
                            mgr.AddSurfaceParameter(name, name, inputDescription, access);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Time _:
                            mgr.AddTimeParameter(name, name, inputDescription, access);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Transform _:
                            mgr.AddTransformParameter(name, name, inputDescription, access);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Vector _:
                            if(input.Default == null)
                                mgr.AddVectorParameter(name, name, inputDescription, access);
                            else
                                mgr.AddVectorParameter(name, name, inputDescription, access, JsonConvert.DeserializeObject<Vector3d>(input.Default.ToString()));
                            break;
                        case Grasshopper.Kernel.Special.GH_NumberSlider _:
                            mgr.AddNumberParameter(name, name, inputDescription, access);
                            break;
                    }
                }

                if (!containsEmptyDefaults)
                    recompute = true;
            }
            if (buildOutputs && outputs != null)
            {
                var mgr = CreateOutputManager();
                foreach (var kv in outputs)
                {
                    string name = kv.Key;
                    var param = kv.Value;
                    switch (param)
                    {
                        case Grasshopper.Kernel.Parameters.Param_Arc _:
                            mgr.AddArcParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Boolean _:
                            mgr.AddBooleanParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Box _:
                            mgr.AddBoxParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Brep _:
                            mgr.AddBrepParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Circle _:
                            mgr.AddCircleParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Colour _:
                            mgr.AddColourParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Complex _:
                            mgr.AddComplexNumberParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Culture _:
                            mgr.AddCultureParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Curve _:
                            mgr.AddCurveParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Field _:
                            mgr.AddFieldParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_FilePath _:
                            mgr.AddTextParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_GenericObject _:
                            throw new Exception("generic param not supported");
                        case Grasshopper.Kernel.Parameters.Param_Geometry _:
                            mgr.AddGeometryParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Group _:
                            throw new Exception("group param not supported");
                        case Grasshopper.Kernel.Parameters.Param_Guid _:
                            throw new Exception("guid param not supported");
                        case Grasshopper.Kernel.Parameters.Param_Integer _:
                            mgr.AddIntegerParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Interval _:
                            mgr.AddIntervalParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Interval2D _:
                            mgr.AddInterval2DParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_LatLonLocation _:
                            throw new Exception("latlonlocation param not supported");
                        case Grasshopper.Kernel.Parameters.Param_Line _:
                            mgr.AddLineParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Matrix _:
                            mgr.AddMatrixParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Mesh _:
                            mgr.AddMeshParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_MeshFace _:
                            mgr.AddMeshFaceParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_MeshParameters _:
                            throw new Exception("meshparameters paran not supported");
                        case Grasshopper.Kernel.Parameters.Param_Number _:
                            mgr.AddNumberParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        //case Grasshopper.Kernel.Parameters.Param_OGLShader:
                        case Grasshopper.Kernel.Parameters.Param_Plane _:
                            mgr.AddPlaneParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Point _:
                            mgr.AddPointParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Rectangle _:
                            mgr.AddRectangleParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        //case Grasshopper.Kernel.Parameters.Param_ScriptVariable _:
                        case Grasshopper.Kernel.Parameters.Param_String _:
                            mgr.AddTextParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_StructurePath _:
                            mgr.AddPathParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_SubD _:
                            mgr.AddSubDParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Surface _:
                            mgr.AddSurfaceParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Time _:
                            mgr.AddTimeParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Transform _:
                            mgr.AddTransformParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                        case Grasshopper.Kernel.Parameters.Param_Vector _:
                            mgr.AddVectorParameter(name, name, name, GH_ParamAccess.tree);
                            break;
                    }
                }
            }

            if (customIcon != null)
            {
                // Draw hops icon overlay on custom icon. We can add an option
                // to the data returned from a server to skip this overlay in
                // the future.
                // Create a slightly large image so we can cram the hops overlay
                // deeper into the lower right corner
                //var bmp = new System.Drawing.Bitmap(28, 28);
                //using(var graphics = System.Drawing.Graphics.FromImage(bmp))
                //{
                //    // use fill to debug
                //    //graphics.FillRectangle(System.Drawing.Brushes.PowderBlue, 0, 0, 28, 28);
                //    var rect = new System.Drawing.Rectangle(2, 2, 24, 24);
                //    graphics.DrawImage(customIcon, rect);
                //    rect = new System.Drawing.Rectangle(16, 14, 14, 14);
                //    graphics.DrawImage(Hops24Icon(), rect);

                //}
                SetIconOverride(customIcon);
            }
            if (buildInputs || buildOutputs)
            {
                Params.OnParametersChanged();
                Grasshopper.Instances.ActiveCanvas?.Invalidate();

                if (recompute)
                    OnPingDocument().NewSolution(true);
            }
        }

        GH_InputParamManager CreateInputManager()
        {
            var constructors = typeof(GH_InputParamManager).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var mgr = constructors[0].Invoke(new object[] { this }) as GH_InputParamManager;
            return mgr;
        }
        GH_OutputParamManager CreateOutputManager()
        {
            var constructors = typeof(GH_OutputParamManager).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var mgr = constructors[0].Invoke(new object[] { this }) as GH_OutputParamManager;
            return mgr;
        }
  
    }
}
