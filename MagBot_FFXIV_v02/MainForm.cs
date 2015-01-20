﻿using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MagBot_FFXIV_v02.Properties;
using Timer = System.Timers.Timer;

namespace MagBot_FFXIV_v02
{
    public partial class MainForm : Form
    {
        //Should really be loaded from config file
        private const string ProcessName = "ffxiv";
        private const string FFXIVFolder = @"My Games\FINAL FANTASY XIV - A Realm Reborn\Bot Config Files";

        private bool _getProcessResult;
        private Player _player;
        private ExpFarming _expFarming;
        private Worker _bw;
        private ChatLogHandler _chatLogHandler;
        private Thread _expFarmingThread;
        private Timer _endExpFarmingTimer;
        private int _expFarmingSecondsPassed;

        public SynchronizationContext UISynchContext { get; private set; }

        //When stopping exp farming, this delegate can be invoked to fix button visibility
        //We here demonstrate the use of the predeclared Action delegate with lambda expression (to make it anonymous)
        public readonly Action SEFDelegate;

        public static MainForm Get { get; private set; }

        private bool ClosePending { get; set; }

        public MainForm()
        {
            InitializeComponent();
            Get = this;
            UISynchContext = SynchronizationContext.Current;

            SEFDelegate = () =>
            {
                Globals.Instance.ExpFarmingLogger.Log("SEFDelegate called, fixing control visibility...");
                EnableChildrenControls(ExpFarmingTab);
                _endExpFarmingTimer.Stop();
                btStopExpFarming.Enabled = false;
            };
        }

        public static string FFXIVFolderPath { get; private set; }

        private void MainForm_Load(object sender, EventArgs e)
        {
            //1)Make sure config folder exist and start application Logging
            string documentsDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            FFXIVFolderPath = Path.Combine(documentsDir, FFXIVFolder);
            if (!Directory.Exists(FFXIVFolderPath))
            {
                var message = "FFXIV config directory (" + FFXIVFolderPath + ") does not exist, please select new folder...";
                Globals.Instance.ShowMessage(message);

                // Show the FolderBrowserDialog
                var folderBrowserDialog1 = new FolderBrowserDialog();
                if (folderBrowserDialog1.ShowDialog() == DialogResult.OK)
                {
                    FFXIVFolderPath = folderBrowserDialog1.SelectedPath;
                }
                else
                {
                    Application.Exit();
                    return;
                }
            }
            Globals.Instance.ApplicationLogger = new Logger("ApplicationLog");

            //2 Load XML Files and initialize other globals dictionaries
            Globals.Instance.InitializeDictionaries();

            //3) Find, open and attach to selected process
            ProcessInfo[] pi;
            _getProcessResult = ProcessInfo.Get(ProcessName, out pi);
            if (_getProcessResult && (pi.Length == 1))
            {
                MemoryHandler.Instance.Initialize(pi[0].Id, null);
            }
            else
            {
                const string message = "Could not load process, exiting application...";
                Globals.Instance.ShowMessage(message);
                Globals.Instance.ApplicationLogger.Log(message);
                Application.Exit();
                return;
            }

            //4) Create KeySender instance
            Globals.Instance.KeySenderInstance = new KeySender(MemoryHandler.Instance.MainWindowHandle);

            //5) Create Player instance
            _player = new Player(Globals.Instance.MemoryBaseOffsetDictionary["Player"]);

            //Check that Memory Pointers have not changed
            if (!(_player.HP > 10 && _player.HP < 10000))
            {
                const string message = "Memory addresses have changed, please update XML file(s). Exiting application...";
                Globals.Instance.ShowMessage(message);
                Globals.Instance.ApplicationLogger.Log(message);
                Application.Exit();
            }

            //Start ChatLogHandler
            _chatLogHandler = new ChatLogHandler();
        }

        private void tab_SelectedIndexChanged(object sender, EventArgs e)
        {
            var page = sender as TabPage;
            
            //Create new expFarming instance and start updating GUI
            if (page != null && _expFarming == null && page.Name == "ExpFarming")
            {
                _expFarming = new ExpFarming(_player);
            }
        }

        private void btStartExpFarming_Click(object sender, EventArgs e)
        {
            //Disables all controls except for stop button
            DisableChildrenControls(ExpFarmingTab);
            EnableControl(btStopExpFarming);

            //Set up timer to end expFarming automatically
            var secondsToRun = Convert.ToInt32(nudHours.Text) * 3600 + Convert.ToInt32(nudMinutes.Text) * 60;
            _expFarmingSecondsPassed = 0;

            _endExpFarmingTimer = new Timer { Interval = 1000, AutoReset = true }; //Ticks every second
            var selectedEscapeRoute = RouteManager.Instance.Routes[cbEscapeRoutes.SelectedIndex];

            //Just takes the first row in selection, if selected several rows. SubItem[0] is the waypoint index
            if (cbStandStill.Checked)
            {
                _expFarmingThread = new Thread(() => _expFarming.StartStandStillExpFarming(selectedEscapeRoute));
                _endExpFarmingTimer.Elapsed += (s, a) => OnTimedEvent_ExpFarmingDone(secondsToRun);
            }
            else
            {
                var selectedRoute = RouteManager.Instance.Routes[Convert.ToInt32(lvWaypoints.SelectedItems[0].Text) - 1];
                _expFarmingThread = new Thread(() => _expFarming.StartExpFarming(selectedRoute, selectedEscapeRoute));
                _endExpFarmingTimer.Elapsed += (s, a) => OnTimedEvent_ExpFarmingDone(secondsToRun, selectedEscapeRoute);
            }
            _expFarmingThread.IsBackground = true;
            _expFarmingThread.Name = "ExpFarming Thread";
            _expFarmingThread.Start();
            _endExpFarmingTimer.Start();
        }

        private void OnTimedEvent_ExpFarmingDone(int totalSecondsToRun, Route escapeRoute = null)
        {
            _expFarmingSecondsPassed ++;
            var ts = TimeSpan.FromSeconds(_expFarmingSecondsPassed);
            var timeElapsed = string.Format("{0:D2}h:{1:D2}m:{2:D2}s", ts.Hours, ts.Minutes, ts.Seconds);
            UISynchContext.Send(o => lbExpFarmingElapsedTime.Text = timeElapsed, null);
            Console.WriteLine(@"Seconds passed: {0}, Seconds to run: {1}", _expFarmingSecondsPassed, totalSecondsToRun);
            if (_expFarmingSecondsPassed != totalSecondsToRun) return;
            
            _endExpFarmingTimer.Stop();
            Globals.Instance.ExpFarmingLogger.Log("Exp farming timer up, proceeding to escape route...");
            _expFarming.StopApp();
            _expFarmingThread.Join();
            _expFarming.Cancel.Reset();
            if (escapeRoute != null)
            {
                Task.Run(() =>
                {
                    _expFarming.InitiateEscape(escapeRoute, false, _expFarming.Cancel);
                    _expFarming.Cancel.WaitOne(20000);
                    Globals.Instance.KeySenderInstance.SendKey(Keys.D1, KeySender.ModifierControl);
                    _expFarming.Cancel.WaitOne(1000);
                    Globals.Instance.KeySenderInstance.SendKey(Keys.NumPad0);
                    _expFarming.Cancel.WaitOne(1000);
                    Globals.Instance.KeySenderInstance.SendKey(Keys.NumPad4);
                    _expFarming.Cancel.WaitOne(500);
                    Globals.Instance.KeySenderInstance.SendKey(Keys.NumPad0);
                    _expFarming.Cancel.WaitOne(30000);
                    Globals.Instance.KeySenderInstance.SendKey(Keys.D0, KeySender.ModifierControl);
                    _expFarming.Cancel.WaitOne(1000);
                    Globals.Instance.KeySenderInstance.SendKey(Keys.NumPad0);
                    _expFarming.Cancel.WaitOne(1000);
                    Globals.Instance.KeySenderInstance.SendKey(Keys.NumPad4);
                    _expFarming.Cancel.WaitOne(1000);
                    Globals.Instance.KeySenderInstance.SendKey(Keys.NumPad0);
                    _expFarming.Cancel.Set();
                });
            }
        }

        private void btStopExpFarming_Click(object sender, EventArgs e)
        {
            _expFarming.StopApp();
        }

        private void btNewRoute_Click(object sender, EventArgs e)
        {
            RouteManager.Instance.Routes.Add(new Route("Route " + (RouteManager.Instance.Routes.Count + 1)));
            RecordWaypoint(RouteManager.Instance.Routes.Last());
            btRecWaypoint.Enabled = true;
        }

        private void lvWaypoints_SelectedIndexChanged(object sender, EventArgs e)
        {
            //If nothing is selected then you shouldn't be allowed to delete.
            //Only start exp farming if you have 2 or more points
            //Start exp farming from selected waypoint, if none is selected then disable the button
            //If cbStandStill is checked, it does not matter if anything is selected 
            if (cbStandStill.Checked) return;

            if (lvWaypoints.SelectedItems.Count > 0)
            {
                var selectedRoute = Convert.ToInt32(lvWaypoints.SelectedItems[0].Text) - 1;
                btStartExpFarming.Enabled = RouteManager.Instance.Routes[selectedRoute].Points.Count > 2;
            }
            else
            {
                btStartExpFarming.Enabled = false;
            }
        }

        private void cbStandStill_CheckChanged(object sender, EventArgs e)
        {
            var cb = sender as CheckBox;
            if (cb != null && cb.Checked)
            {
                if (cbEscapeRoutes.Items.Count == 0)
                {
                    Globals.Instance.ShowMessage("Please select an escape route first.");
                    cb.Checked = false;
                    return;
                }
                var escapeRoute = RouteManager.Instance.Routes[cbEscapeRoutes.SelectedIndex];
                btStartExpFarming.Enabled = escapeRoute.Points.Count > 2;
            }
        }

        private void btRecWaypoint_Click(object sender, EventArgs e)
        {
            RecordWaypoint(RouteManager.Instance.Routes.Last());
        }

        private void btDelWaypoint_Click(object sender, EventArgs e)
        {
            for (var i = 0; i < lvWaypoints.Items.Count; i++)
            {
                if (!lvWaypoints.Items[i].Selected) continue;
                Route route = RouteManager.Instance.Routes[Convert.ToInt32(lvWaypoints.Items[i].SubItems[0].Text) - 1];
                DelWaypoint(route, route.Points[Convert.ToInt32(lvWaypoints.Items[i].SubItems[1].Text) - 1]);
            }
            if (lvWaypoints.Items.Count == 0) btRecWaypoint.Enabled = false;
        }

        private void UpdateExpFarmingControls()
        {
            cbEscapeRoutes.Items.Clear();

            for (var i = 0; i < lvWaypoints.Items.Count; i++)
            {
                lvWaypoints.Items[i].Remove();
                i--;
            }

            for (var i = 0; i < RouteManager.Instance.Routes.Count; i++)
            {
                for (var j = 0; j < RouteManager.Instance.Routes[i].Points.Count; j++)
                {
                    var lvi = new ListViewItem((i + 1).ToString(CultureInfo.InvariantCulture), 0);
                    lvi.SubItems.Add((j + 1).ToString(CultureInfo.InvariantCulture));
                    lvi.SubItems.Add(RouteManager.Instance.Routes[i].Points[j].X.ToString(CultureInfo.InvariantCulture));
                    lvi.SubItems.Add(RouteManager.Instance.Routes[i].Points[j].Y.ToString(CultureInfo.InvariantCulture));
                    lvi.SubItems.Add(RouteManager.Instance.Routes[i].Points[j].Z.ToString(CultureInfo.InvariantCulture));
                    lvWaypoints.Items.Add(lvi);
                }

                cbEscapeRoutes.Items.Add(RouteManager.Instance.Routes[i].Name);
            }

            if (lvWaypoints.Items.Count <= 0) return;
            cbEscapeRoutes.SelectedIndex = 0;
            lvWaypoints.Items[0].Selected = true;
            btRecWaypoint.Enabled = true;
            btSaveRoutes.Enabled = true;
        }

        private void btLoadRoutes_Click(object sender, EventArgs e)
        {
            var openFileDialog1 = new OpenFileDialog
            {
                Filter = Resources.MainForm_btLoadRoutes_Click_XML_Files___xml____xml,
                FilterIndex = 1,
                Multiselect = false
            };

            if (openFileDialog1.ShowDialog() != DialogResult.OK) return;
            RouteManager.Instance.LoadRouteManager(openFileDialog1.FileName);
            UpdateExpFarmingControls();
        }

        private void btSaveRoutes_Click(object sender, EventArgs e)
        {
            var saveFileDialog1 = new SaveFileDialog
            {
                Filter = Resources.MainForm_btSaveRoutes_Click_XML_files____xml____xml,
                DefaultExt = "xml",
                Title = Resources.MainForm_btSaveRoutes_Click_Save
            };
            if (saveFileDialog1.ShowDialog() == DialogResult.OK)
            {
                RouteManager.Instance.Save(saveFileDialog1.FileName);
            }
        }

        private void RecordWaypoint(Route route)
        {
            var wp = _player.WaypointLocation;

            if (route.Points.Count > 0)
            {
                for (var i = 0; i < route.Points.Count; i++)
                {
                    double distanceBetweenTargets = route.Points[i].Distance(wp);

                    if (!(distanceBetweenTargets < 4)) continue;
                    MessageBox.Show(Resources.ExpFarming_RecordWaypoint_Point_too_close_to_one_of_the_other_points_,
                        Resources.ExpFarming_RecordWaypoint_Error,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Exclamation,
                        MessageBoxDefaultButton.Button1);
                    return;
                }
            }

            route.Points.Add(wp);
            UpdateExpFarmingControls();
        }

        private void DelWaypoint(Route route, Waypoint waypoint)
        {
            route.Points.Remove(waypoint);
            if (route.Points.Count == 0)
            {
                RouteManager.Instance.Routes.Remove(route);
            }
            UpdateExpFarmingControls();
            if (RouteManager.Instance.Routes.Count == 0) btStartExpFarming.Enabled = false;
        }

        //Not ideal way to communicate with UI, because
        //1) UI thread has to deal with logic that depends on what thread the method was called from
        //2) If My worker class that calls this method was a library, it means that it would have to assume that
        //it is used with a Windows Form because WPF and Metro etc does not use Invoke, they use SynchronizationContext
        //And by using SynchronizationContext you can avoid this code and just use UpdateText(), below
        public void UIThreadInvoke(Control control, Action code)
        {
            if (control.InvokeRequired)
            {
                control.Invoke(code);
                return;
            }
            code.Invoke();
        }

        public void UpdateText(string labelName, string text)
        {
            var label = (Label)Controls.Find(labelName, true).FirstOrDefault();
            if (label != null) label.Text = text;
        }

        public void StopScanning()
        {
            if (!ClosePending) return;
            const string message = "Worker-thread is closing the application...";
            Globals.Instance.ApplicationLogger.Log(message);
            Close();
        }

        private void DisableChildrenControls(Control con)
        {
            foreach (Control c in con.Controls)
            {
                DisableChildrenControls(c);
            }
            con.Enabled = false;
        }

        private void EnableChildrenControls(Control con)
        {
            foreach (Control c in con.Controls)
            {
                EnableChildrenControls(c);
            }
            con.Enabled = true;
        }

        private void EnableControl(Control con)
        {
            if (con != null)
            {
                con.Enabled = true;
                EnableControl(con.Parent);
            }
        }

        private void checkValuesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var hp = _player.HP.ToString(CultureInfo.InvariantCulture);
            var maxHp = _player.MaxHP.ToString(CultureInfo.InvariantCulture);
            var mp = _player.MP.ToString(CultureInfo.InvariantCulture);
            var maxMp = _player.MaxMP.ToString(CultureInfo.InvariantCulture);
            var tp = _player.TP.ToString(CultureInfo.InvariantCulture);
            var maxTp = _player.MaxTP.ToString(CultureInfo.InvariantCulture);
            var xCoor = _player.XCoordinate.ToString(CultureInfo.InvariantCulture);
            var yCoor = _player.YCoordinate.ToString(CultureInfo.InvariantCulture);
            var zCoor = _player.ZCoordinate.ToString(CultureInfo.InvariantCulture);
            var facing = _player.FacingAngle.ToString(CultureInfo.InvariantCulture);

            var combinedMessage = "HP: " + hp + "/" + maxHp + Environment.NewLine + "MP: " + mp + "/" + maxMp + Environment.NewLine + "TP: " + tp + "/" + maxTp + Environment.NewLine + "X, Y, Z: " + xCoor + ", " + yCoor + ", " + zCoor + Environment.NewLine + "Facing direction (radians): " + facing;

            Globals.Instance.ShowMessage(combinedMessage);
        }

        public string ChatLog
        {
            get { return lbChatLog.Text; }
            set { lbChatLog.Items.Add(value); }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            var button = sender as Button;
            if (button != null && string.Equals(button.Name, @"CloseButton"))
            {
                //FormClosing event raised by a user created button action
                //const string message = "Form closing by user action, exiting application...";
                //Globals.Instance.ShowMessage(message);
                //Globals.Instance.LoggerDictionary["ApplicationLog"].Log(message);
            }
            else
            {
                //FormClosing event raised by program or the X in top right corner
                //Do cleanup work (stop threads and clean up unmanaged resources)

                if (_bw != null && !_bw.MRE.WaitOne(0))
                {
                    _bw.MRE.Set(); //This exits the loop
                    ClosePending = true;
                    e.Cancel = true;
                    return;
                }

                if (Globals.Instance.ApplicationLogger != null) Globals.Instance.ApplicationLogger.Log("FormClosing event raised by program. Cleaning up resources, then exiting application...");

                if (_expFarming != null)
                {
                    if (_expFarmingThread != null && !_expFarming.Cancel.WaitOne(0)) btStopExpFarming_Click(sender, e);
                    if (_expFarmingThread != null && _expFarmingThread.IsAlive) _expFarmingThread.Join(); //Wait with disposing ExpFarmingLogger until thread is terminated
                    Globals.Instance.ExpFarmingLogger.Dispose();
                }

                MemoryHandler.Instance.Dispose();
                if (Globals.Instance.ApplicationLogger != null) Globals.Instance.ApplicationLogger.Dispose();
            }
        }
    }
}