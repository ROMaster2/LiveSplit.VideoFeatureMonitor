﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using LiveSplit.Model;
using LiveSplit.Options;
using LiveSplit.UI;
using LiveSplit.UI.Components;
using LiveSplit.VAS.Models;
using LiveSplit.VAS.Models.Delta;
using LiveSplit.VAS.UI;
using LiveSplit.VAS.VASL;

namespace LiveSplit.VAS
{
    public class VASComponent : LogicComponent
    {
        public override string ComponentName => "Video Auto Splitter";

        private readonly LiveSplitState _LiveSplitState;
        private readonly ComponentUI _ComponentUI;
        private readonly FileSystemWatcher _FSWatcher;

        // Is this name okay? It feels like it conflicts with existing standards, but I don't know exactly which ones.
        private string _EventLog = string.Empty;
        private string _ProfilePath = string.Empty;
        private string _GameVersion = string.Empty;
        private string _VideoDevice = string.Empty;

        private GameProfile _GameProfile;
        private VASLScript _Script;

        // Temporary. Remove later.
        public Geometry CropGeometry { get { return Scanner.CropGeometry; } set { Scanner.CropGeometry = value; } }
        public Scanner Scanner { get; internal set; }

        public IDictionary<string, Geometry> CropGeometries { get; internal set; }
        public IDictionary<string, bool> BasicSettingsState { get; internal set; }
        public IDictionary<string, dynamic> CustomSettingsState { get; internal set; }

        public event EventHandler<GameProfile> ProfileChanged;
        public event EventHandler<string> VideoDeviceChanged;
        public event EventHandler<string> GameVersionChanged;
        public event EventHandler<string> EventLogUpdated;
        //public event EventHandler<Geometry> CropGeometryChanged;

        public string ProfilePath
        {
            get
            {
                return _ProfilePath;
            }
            set
            {
                if (_ProfilePath != value)
                {
                    // This is so the event logger for ProfileCleanup doesn't run at start.
                    if (!string.IsNullOrEmpty(_ProfilePath))
                    {
                        ProfileCleanup();
                    }
                    try
                    {
                        _ProfilePath = value;
                        ProfileChanged?.Invoke(this, GameProfile); // Invokes getter
                    }
                    catch (Exception e)
                    {
                        LogEvent("Test1234"); // Testing to see if it appears when it's not supposed to.
                        LogEvent(e);
                        _ProfilePath = string.Empty;
                        ProfileCleanup();
                    }
                }
            }
        }

        public GameProfile GameProfile
        {
            get
            {
                if (_GameProfile == null)
                {
                    try
                    {
                        LogEvent("Loading new game profile: " + ProfilePath);

                        _GameProfile = GameProfile.FromPath(ProfilePath);
                        // @TODO: Unset GameVersion if the existing one isn't in the new profile. Maybe.

                        if (File.Exists(ProfilePath))
                        {
                            _FSWatcher.Path = Path.GetDirectoryName(ProfilePath);
                            _FSWatcher.Filter = Path.GetFileName(ProfilePath + "*");
                        }
                        else
                        {
                            _FSWatcher.Path = ProfilePath;
                            _FSWatcher.Filter = null;
                        }

                        _FSWatcher.EnableRaisingEvents = true;
                        LogEvent("Game profile successfully loaded!");

                        // Todo: Log this separately. Just don't want to atm.
                        VASLSettings settings = Script.RunStartup(_LiveSplitState);
                        SetVASLSettings(settings);
                    }
                    catch (Exception e)
                    {
                        LogEvent("Error loading Game profile:");
                        LogEvent(e);
                        ProfileCleanup();
                    }
                }
                return _GameProfile;
            }
        }

        public VASLScript Script
        {
            get
            {
                if (_Script == null)
                {
                    try
                    {
                        var gp = GameProfile; // Invoke getter
                        LogEvent("Loading VASL script within profile...");
                        _Script = new VASLScript(gp.RawScript);
                        LogEvent("VASL script successfully loaded!");
                        TryStartScanner();
                    }
                    catch (Exception e)
                    {
                        LogEvent("Error loading VASL script:");
                        LogEvent(e);
                        ProfileCleanup();
                    }
                }
                return _Script;
            }
        }

        public string GameVersion
        {
            get
            {
                return _GameVersion;
            }
            internal set
            {
                if (_GameVersion != value)
                {
                    _GameVersion = value;
                    GameVersionChanged?.Invoke(this, _GameVersion);
                }
            }
        }

        public string VideoDevice
        {
            get
            {
                return _VideoDevice;
            }
            internal set
            {
                if (_VideoDevice != value)
                {
                    _VideoDevice = value;
                    VideoDeviceChanged?.Invoke(this, _VideoDevice);
                    TryStartScanner();
                }
            }
        }

        public string EventLog
        {
            get
            {
                return _EventLog;
            }
            private set
            {
                var str = "[" + DateTime.Now.ToString("hh:mm:ss.fff") + "] " + value + "\r\n";
                _EventLog += str;
                EventLogUpdated?.Invoke(this, str);
            }
        }

        public VASComponent(LiveSplitState state)
        {
            LogEvent("Establishing Video Auto Splitter, standby...");

            _LiveSplitState = state;

            _ComponentUI = new ComponentUI(this);

            CropGeometries = new Dictionary<string, Geometry>();
            BasicSettingsState = new Dictionary<string, bool>();
            CustomSettingsState = new Dictionary<string, dynamic>();

            Scanner = new Scanner(this);

            _FSWatcher = new FileSystemWatcher();
            _FSWatcher.Changed += async (sender, args) =>
            {
                await Task.Delay(200).ConfigureAwait(false);
                ProfileCleanup();
            };

            Scanner.NewResult += (sender, dm) => RunScript(sender, dm);
        }

        public override Control GetSettingsControl(LayoutMode mode) => _ComponentUI;

        #region Save Settings

        public override XmlNode GetSettings(XmlDocument document)
        {
            XmlElement settingsNode = document.CreateElement("Settings");

            settingsNode.AppendChild(SettingsHelper.ToElement(document, "Version", "0.1"));
            settingsNode.AppendChild(SettingsHelper.ToElement(document, "ProfilePath", ProfilePath));
            settingsNode.AppendChild(SettingsHelper.ToElement(document, "VideoDevice", VideoDevice));
            settingsNode.AppendChild(SettingsHelper.ToElement(document, "GameVersion", GameVersion));
            settingsNode.AppendChild(SettingsHelper.ToElement(document, "CropGeometry", CropGeometry));

            AppendBasicSettingsToXml(document, settingsNode);
            AppendCustomSettingsToXml(document, settingsNode);

            return settingsNode;
        }

        private void AppendBasicSettingsToXml(XmlDocument document, XmlNode settingsNode)
        {
            var basicSettings = _ComponentUI?.SettingsUI?.BasicSettings;
            if (basicSettings?.Count > 0)
            {
                foreach (var setting in basicSettings)
                {
                    var key = setting.Key;
                    if (BasicSettingsState.ContainsKey(key.ToLower()))
                    {
                        var value = BasicSettingsState[key.ToLower()];
                        settingsNode.AppendChild(SettingsHelper.ToElement(document, key, value));
                    }
                }
            }
        }

        private void AppendCustomSettingsToXml(XmlDocument document, XmlNode parent)
        {
            XmlElement vaslParent = document.CreateElement("CustomSettings");

            foreach (var state in CustomSettingsState)
            {
                XmlElement element = SettingsHelper.ToElement(document, "Setting", state.Value);
                XmlAttribute id = SettingsHelper.ToAttribute(document, "ID", state.Key);

                element.Attributes.Append(id);
                vaslParent.AppendChild(element);
            }

            parent.AppendChild(vaslParent);
        }

        #endregion Save Settings

        #region Load Settings

        public override void SetSettings(XmlNode settings)
        {
            var element = (XmlElement)settings;
            if (!element.IsEmpty)
            {
                ParseStandardSettingsFromXml(element);
                ParseBasicSettingsFromXml(element);
                ParseCustomScriptSettingsFromXml(element);
            }
            //UpdateProfile(null, null);
        }

        private void ParseStandardSettingsFromXml(XmlElement element)
        {
            ProfilePath = SettingsHelper.ParseString(element["ProfilePath"], string.Empty);
            GameVersion = SettingsHelper.ParseString(element["GameVersion"], string.Empty);

            // Hacky? Probably. Geometry should have proper XML support.
            var geo = Geometry.FromString(element["CropGeometry"].InnerText);
            if (geo.HasSize) CropGeometry = geo;

            VideoDevice = SettingsHelper.ParseString(element["VideoDevice"], string.Empty);
        }

        private void ParseBasicSettingsFromXml(XmlElement element)
        {
            var basicSettings = _ComponentUI?.SettingsUI?.BasicSettings;
            if (basicSettings?.Count > 0)
            {
                foreach (var setting in basicSettings)
                {
                    if (element[setting.Key] != null)
                    {
                        var value = bool.Parse(element[setting.Key].InnerText);
                        if (setting.Value.Enabled) setting.Value.Checked = value;
                        BasicSettingsState[setting.Key.ToLower()] = value;
                    }
                }
            }
        }

        private void ParseCustomScriptSettingsFromXml(XmlElement data)
        {
            XmlElement customSettingsNode = data["CustomSettings"];

            if (customSettingsNode?.HasChildNodes == true)
            {
                foreach (XmlElement element in customSettingsNode.ChildNodes)
                {
                    if (element.Name != "Setting") continue;

                    string id = element.Attributes["id"]?.Value;
                    //string type = element.Attributes["type"].Value;
                    const string type = "string"; // Temporary until coercion by the script is done.

                    if (id != null)
                    {
                        dynamic value;

#pragma warning disable CS0162 // Unreachable code detected

                        switch (type)
                        {
                            case "bool":
                                value = SettingsHelper.ParseBool(element);
                                break;
                            case "float":
                            case "double":
                                value = SettingsHelper.ParseDouble(element);
                                break;
                            case "byte":
                            case "sbyte":
                            case "short":
                            case "ushort":
                            case "int":
                            case "uint":
                            case "long":
                            case "ulong":
                                value = SettingsHelper.ParseInt(element);
                                break;
                            case "char":
                            case "string":
                                value = SettingsHelper.ParseString(element);
                                break;
                            case "timespan":
                                value = SettingsHelper.ParseTimeSpan(element);
                                break;
                            default:
                                throw new NotSupportedException("Data type is either incorrect or unsupported.");
                        }

#pragma warning restore CS0162 // Unreachable code detected

                        CustomSettingsState[id] = value;
                    }
                }
            }
            //SettingUI.UpdateNodesValues(CustomSettingsState, null);
        }

        #endregion Load Settings

        #region VASL Settings

        private void InitVASLSettings(VASLSettings settings, bool scriptLoaded)
        {
            if (!scriptLoaded)
            {
                BasicSettingsState.Clear();
                CustomSettingsState.Clear();
            }
            else
            {
                var values = new Dictionary<string, dynamic>();

                foreach (var setting in settings.OrderedSettings)
                {
                    var value = setting.Value;
                    if (CustomSettingsState.ContainsKey(setting.Id))
                    {
                        value = CustomSettingsState[setting.Id];
                    }

                    setting.Value = value;

                    values.Add(setting.Id, value);
                }

                CustomSettingsState = values;
            }

            // sigh...
            if (_ComponentUI.InvokeRequired)
            {
                _ComponentUI.Invoke((MethodInvoker)delegate
                {
                    _ComponentUI.InitVASLSettings(settings, scriptLoaded);
                });
            }
            else
            {
                _ComponentUI.InitVASLSettings(settings, scriptLoaded);
            }
        }

        public void SetVASLSettings(VASLSettings settings) => InitVASLSettings(settings, true);
        public void ResetVASLSettings() => InitVASLSettings(new VASLSettings(), false);

        #endregion VASL Settings

        internal void RunScript(object _, DeltaOutput d)
        {
            try
            {
                Script.Update(_LiveSplitState, d);
            }
            catch (Exception e)
            {
                LogEvent(e);
            }
        }

        // TEMPORARY
        private void ScannerTemp()
        {
            var videoDevices = new Accord.Video.DirectShow.FilterInfoCollection(Accord.Video.DirectShow.FilterCategory.VideoInputDevice);
            var device = videoDevices.Find(x => x.ToString() == VideoDevice);
            var m = device.MonikerString;
            //Scanner.SetVideoSource(device?.ToString());
        }

        private void TryStartScanner()
        {
            try
            {
                Scanner.Stop();
                if (!string.IsNullOrEmpty(VideoDevice))
                {
                    ScannerTemp();
                    Scanner.AsyncStart();
                }
            }
            catch (Exception e)
            {
                LogEvent(e);
                ProfileCleanup();
            }
        }

        private void ProfileCleanup()
        {
            LogEvent("Cleaning up profile...");
            try
            {
                _FSWatcher.EnableRaisingEvents = false;
                if (_Script != null) Script.RunShutdown(_LiveSplitState);
            }
            catch (Exception e)
            {
                LogEvent(e);
            }
            finally
            {
                _GameProfile = null;
                _Script = null;
                ResetVASLSettings();
            }
            LogEvent("Profile cleanup finished.");
        }

        // Todo: Use TraceListener instead?
        internal void LogEvent(Exception e)
        {
            Log.Error(e);
            EventLog = e.ToString();
        }

        internal void LogEvent(string message)
        {
            Log.Info("[VAS] " + message);
            EventLog = message;
        }

        internal void ClearEventLog()
        {
            _EventLog = string.Empty;
        }

        public override void Dispose()
        {
            LogEvent("Disposing...");
            //Scanner.Stop();
            ProfileCleanup();
            _FSWatcher?.Dispose();
            LogEvent("Closing...");
        }

        public override void Update(IInvalidator invalidator, LiveSplitState state, float width, float height, LayoutMode mode) { }
    }
}
