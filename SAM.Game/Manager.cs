/* Copyright (c) 2024 Rick (rick 'at' gibbed 'dot' us)
 *
 * This software is provided 'as-is', without any express or implied
 * warranty. In no event will the authors be held liable for any damages
 * arising from the use of this software.
 *
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 *
 * 1. The origin of this software must not be misrepresented; you must not
 *    claim that you wrote the original software. If you use this software
 *    in a product, an acknowledgment in the product documentation would
 *    be appreciated but is not required.
 *
 * 2. Altered source versions must be plainly marked as such, and must not
 *    be misrepresented as being the original software.
 *
 * 3. This notice may not be removed or altered from any source
 *    distribution.
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows.Forms;
using static SAM.Game.InvariantShorthand;
using APITypes = SAM.API.Types;

namespace SAM.Game
{
    internal partial class Manager : Form
    {
        private readonly long _GameId;
        private readonly API.Client _SteamClient;

        private readonly WebClient _IconDownloader = new();

        private readonly List<Stats.AchievementInfo> _IconQueue = new();
        private readonly List<Stats.StatDefinition> _StatDefinitions = new();

        private readonly List<Stats.AchievementDefinition> _AchievementDefinitions = new();

        private readonly BindingList<Stats.StatInfo> _Statistics = new();

        private readonly API.Callbacks.UserStatsReceived _UserStatsReceivedCallback;

        // --- Schedule state ---
        private readonly Random _Random = new Random();
        private int _ScheduleOrderCounter = 0;
        private List<(Stats.AchievementInfo Info, int DelayMs)> _ScheduleQueue;
        private int _ScheduleIndex;
        private DateTime _NextUnlockTime;

        // --- Global percent polling ---
        private int _GlobalPercentRetryCount = 0;
        private readonly Dictionary<string, float> _GlobalPercentCache = new();

        // --- Sort state ---
        private int _SortColumn = -1;
        private SortOrder _SortOrder = SortOrder.None;

        // --- Inline delay editor ---
        private TextBox _InlineEditBox;
        private ListViewItem _EditingItem;
        private int _EditingSubItemIndex;

        public Manager(long gameId, API.Client client)
        {
            this.InitializeComponent();

            this._MainTabControl.SelectedTab = this._AchievementsTabPage;

            this._AchievementImageList.Images.Add("Blank", new Bitmap(64, 64));

            this._StatisticsDataGridView.AutoGenerateColumns = false;

            this._StatisticsDataGridView.Columns.Add("name", "Name");
            this._StatisticsDataGridView.Columns[0].ReadOnly = true;
            this._StatisticsDataGridView.Columns[0].Width = 200;
            this._StatisticsDataGridView.Columns[0].DataPropertyName = "DisplayName";

            this._StatisticsDataGridView.Columns.Add("value", "Value");
            this._StatisticsDataGridView.Columns[1].ReadOnly = this._EnableStatsEditingCheckBox.Checked == false;
            this._StatisticsDataGridView.Columns[1].Width = 90;
            this._StatisticsDataGridView.Columns[1].DataPropertyName = "Value";

            this._StatisticsDataGridView.Columns.Add("extra", "Extra");
            this._StatisticsDataGridView.Columns[2].ReadOnly = true;
            this._StatisticsDataGridView.Columns[2].Width = 200;
            this._StatisticsDataGridView.Columns[2].DataPropertyName = "Extra";

            this._StatisticsDataGridView.DataSource = new BindingSource()
            {
                DataSource = this._Statistics,
            };

            this._GameId = gameId;
            this._SteamClient = client;

            this._IconDownloader.DownloadDataCompleted += this.OnIconDownload;

            string name = this._SteamClient.SteamApps001.GetAppData((uint)this._GameId, "name");
            if (name != null)
            {
                base.Text += " | " + name;
            }
            else
            {
                base.Text += " | " + this._GameId.ToString(CultureInfo.InvariantCulture);
            }

            this._UserStatsReceivedCallback = client.CreateAndRegisterCallback<API.Callbacks.UserStatsReceived>();
            this._UserStatsReceivedCallback.OnRun += this.OnUserStatsReceived;

            // Setup inline delay editor
            this._InlineEditBox = new TextBox
            {
                Visible = false,
                BorderStyle = BorderStyle.FixedSingle,
            };
            this._AchievementListView.Controls.Add(this._InlineEditBox);
            this._InlineEditBox.LostFocus += this.InlineEditBox_LostFocus;
            this._InlineEditBox.KeyDown   += this.InlineEditBox_KeyDown;

            // Tray icon — reuse the form's own icon
            this._TrayIcon.Icon = this.Icon;
            this.Resize += this.OnFormResize;

            this.RefreshStats();
        }

        private void AddAchievementIcon(Stats.AchievementInfo info, Image icon)
        {
            if (icon == null)
            {
                info.ImageIndex = 0;
            }
            else
            {
                info.ImageIndex = this._AchievementImageList.Images.Count;
                this._AchievementImageList.Images.Add(info.IsAchieved == true ?
                    info.IconNormal : info.IconLocked, icon);
            }
        }

        private void OnIconDownload(object sender, DownloadDataCompletedEventArgs e)
        {
            if (e.Error == null && e.Cancelled == false)
            {
                var info = (Stats.AchievementInfo)e.UserState;

                Bitmap bitmap;
                try
                {
                    using (MemoryStream stream = new())
                    {
                        stream.Write(e.Result, 0, e.Result.Length);
                        bitmap = new(stream);
                    }
                }
                catch (Exception)
                {
                    bitmap = null;
                }

                this.AddAchievementIcon(info, bitmap);
                this._AchievementListView.Update();
            }

            this.DownloadNextIcon();
        }

        private void DownloadNextIcon()
        {
            if (this._IconQueue.Count == 0)
            {
                this._DownloadStatusLabel.Visible = false;
                return;
            }

            if (this._IconDownloader.IsBusy == true)
            {
                return;
            }

            this._DownloadStatusLabel.Text = $"Downloading {this._IconQueue.Count} icons...";
            this._DownloadStatusLabel.Visible = true;

            var info = this._IconQueue[0];
            this._IconQueue.RemoveAt(0);

            this._IconDownloader.DownloadDataAsync(
                new Uri(_($"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{this._GameId}/{(info.IsAchieved == true ? info.IconNormal : info.IconLocked)}")),
                info);
        }

        private static string TranslateError(int id) => id switch
        {
            2 => "generic error -- this usually means you don't own the game",
            _ => _($"{id}"),
        };

        private static string GetLocalizedString(KeyValue kv, string language, string defaultValue)
        {
            var name = kv[language].AsString("");
            if (string.IsNullOrEmpty(name) == false)
            {
                return name;
            }

            if (language != "english")
            {
                name = kv["english"].AsString("");
                if (string.IsNullOrEmpty(name) == false)
                {
                    return name;
                }
            }

            name = kv.AsString("");
            if (string.IsNullOrEmpty(name) == false)
            {
                return name;
            }

            return defaultValue;
        }

        private bool LoadUserGameStatsSchema()
        {
            string path;
            try
            {
                string fileName = _($"UserGameStatsSchema_{this._GameId}.bin");
                path = API.Steam.GetInstallPath();
                path = Path.Combine(path, "appcache", "stats", fileName);
                if (File.Exists(path) == false)
                {
                    return false;
                }
            }
            catch (Exception)
            {
                return false;
            }

            var kv = KeyValue.LoadAsBinary(path);
            if (kv == null)
            {
                return false;
            }

            var currentLanguage = this._SteamClient.SteamApps008.GetCurrentGameLanguage();

            this._AchievementDefinitions.Clear();
            this._StatDefinitions.Clear();

            var stats = kv[this._GameId.ToString(CultureInfo.InvariantCulture)]["stats"];
            if (stats.Valid == false || stats.Children == null)
            {
                return false;
            }

            foreach (var stat in stats.Children)
            {
                if (stat.Valid == false)
                {
                    continue;
                }

                APITypes.UserStatType type;

                var typeNode = stat["type"];
                if (typeNode.Valid == true && typeNode.Type == KeyValueType.String)
                {
                    if (Enum.TryParse((string)typeNode.Value, true, out type) == false)
                    {
                        type = APITypes.UserStatType.Invalid;
                    }
                }
                else
                {
                    type = APITypes.UserStatType.Invalid;
                }

                if (type == APITypes.UserStatType.Invalid)
                {
                    var typeIntNode = stat["type_int"];
                    var rawType = typeIntNode.Valid == true
                        ? typeIntNode.AsInteger(0)
                        : typeNode.AsInteger(0);
                    type = (APITypes.UserStatType)rawType;
                }

                switch (type)
                {
                    case APITypes.UserStatType.Invalid:
                    {
                        break;
                    }

                    case APITypes.UserStatType.Integer:
                    {
                        var id = stat["name"].AsString("");
                        string name = GetLocalizedString(stat["display"]["name"], currentLanguage, id);

                        this._StatDefinitions.Add(new Stats.IntegerStatDefinition()
                        {
                            Id = stat["name"].AsString(""),
                            DisplayName = name,
                            MinValue = stat["min"].AsInteger(int.MinValue),
                            MaxValue = stat["max"].AsInteger(int.MaxValue),
                            MaxChange = stat["maxchange"].AsInteger(0),
                            IncrementOnly = stat["incrementonly"].AsBoolean(false),
                            SetByTrustedGameServer = stat["bSetByTrustedGS"].AsBoolean(false),
                            DefaultValue = stat["default"].AsInteger(0),
                            Permission = stat["permission"].AsInteger(0),
                        });
                        break;
                    }

                    case APITypes.UserStatType.Float:
                    case APITypes.UserStatType.AverageRate:
                    {
                        var id = stat["name"].AsString("");
                        string name = GetLocalizedString(stat["display"]["name"], currentLanguage, id);

                        this._StatDefinitions.Add(new Stats.FloatStatDefinition()
                        {
                            Id = stat["name"].AsString(""),
                            DisplayName = name,
                            MinValue = stat["min"].AsFloat(float.MinValue),
                            MaxValue = stat["max"].AsFloat(float.MaxValue),
                            MaxChange = stat["maxchange"].AsFloat(0.0f),
                            IncrementOnly = stat["incrementonly"].AsBoolean(false),
                            DefaultValue = stat["default"].AsFloat(0.0f),
                            Permission = stat["permission"].AsInteger(0),
                        });
                        break;
                    }

                    case APITypes.UserStatType.Achievements:
                    case APITypes.UserStatType.GroupAchievements:
                    {
                        if (stat.Children != null)
                        {
                            foreach (var bits in stat.Children.Where(
                                b => string.Compare(b.Name, "bits", StringComparison.InvariantCultureIgnoreCase) == 0))
                            {
                                if (bits.Valid == false || bits.Children == null)
                                {
                                    continue;
                                }

                                foreach (var bit in bits.Children)
                                {
                                    string id = bit["name"].AsString("");
                                    string name = GetLocalizedString(bit["display"]["name"], currentLanguage, id);
                                    string desc = GetLocalizedString(bit["display"]["desc"], currentLanguage, "");

                                    this._AchievementDefinitions.Add(new()
                                    {
                                        Id = id,
                                        Name = name,
                                        Description = desc,
                                        IconNormal = bit["display"]["icon"].AsString(""),
                                        IconLocked = bit["display"]["icon_gray"].AsString(""),
                                        IsHidden = bit["display"]["hidden"].AsBoolean(false),
                                        Permission = bit["permission"].AsInteger(0),
                                    });
                                }
                            }
                        }

                        break;
                    }

                    default:
                    {
                        throw new InvalidOperationException("invalid stat type");
                    }
                }
            }

            return true;
        }

        private void OnUserStatsReceived(APITypes.UserStatsReceived param)
        {
            if (param.Result != 1)
            {
                this._GameStatusLabel.Text = $"Error while retrieving stats: {TranslateError(param.Result)}";
                this.EnableInput();
                return;
            }

            if (this.LoadUserGameStatsSchema() == false)
            {
                this._GameStatusLabel.Text = "Failed to load schema.";
                this.EnableInput();
                return;
            }

            try
            {
                this.GetAchievements();
            }
            catch (Exception e)
            {
                this._GameStatusLabel.Text = "Error when handling achievements retrieval.";
                this.EnableInput();
                MessageBox.Show(
                    "Error when handling achievements retrieval:\n" + e,
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            try
            {
                this.GetStatistics();
            }
            catch (Exception e)
            {
                this._GameStatusLabel.Text = "Error when handling stats retrieval.";
                this.EnableInput();
                MessageBox.Show(
                    "Error when handling stats retrieval:\n" + e,
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            this._GameStatusLabel.Text = $"Retrieved {this._AchievementListView.Items.Count} achievements and {this._StatisticsDataGridView.Rows.Count} statistics.";
            this.EnableInput();

            // Request global percentages only if some entries are still missing from cache
            bool hasPending = this._AchievementListView.Items
                .Cast<ListViewItem>()
                .Any(i => i.SubItems[5].Text == "...");

            if (hasPending)
            {
                this._GlobalPercentRetryCount = 0;
                this._GlobalPercentTimer.Stop();
                this._SteamClient.SteamUserStats.RequestGlobalAchievementPercentages();
                this._GlobalPercentTimer.Start();
            }
        }

        private void OnGlobalPercentTick(object sender, EventArgs e)
        {
            this._GlobalPercentRetryCount++;

            bool anyPending = false;

            foreach (ListViewItem item in this._AchievementListView.Items)
            {
                if (item.Tag is not Stats.AchievementInfo info)
                    continue;

                // Skip already filled entries
                if (item.SubItems[5].Text != "...")
                    continue;

                if (this._SteamClient.SteamUserStats.GetAchievementAchievedPercent(info.Id, out float percent))
                {
                    item.SubItems[5].Text = percent.ToString("0.0") + "%";
                    this._GlobalPercentCache[info.Id] = percent;
                }
                else
                {
                    anyPending = true;
                }
            }

            // Stop after all filled or after 12 attempts (~1 minute at 5s interval)
            if (!anyPending || this._GlobalPercentRetryCount >= 12)
            {
                this._GlobalPercentTimer.Stop();
                this._GlobalPercentRetryCount = 0;

                // Mark any still-pending as unavailable
                if (anyPending)
                {
                    foreach (ListViewItem item in this._AchievementListView.Items)
                    {
                        if (item.SubItems[5].Text == "...")
                            item.SubItems[5].Text = "N/A";
                    }
                }
            }
        }

        private void RefreshStats()
        {
            this._AchievementListView.Items.Clear();
            this._StatisticsDataGridView.Rows.Clear();
            this._ScheduleOrderCounter = 0;
            this._GlobalPercentTimer.Stop();

            var steamId = this._SteamClient.SteamUser.GetSteamId();

            var callHandle = this._SteamClient.SteamUserStats.RequestUserStats(steamId);
            if (callHandle == API.CallHandle.Invalid)
            {
                MessageBox.Show(this, "Failed.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            this._GameStatusLabel.Text = "Retrieving stat information...";
            this.DisableInput();
        }

        private bool _IsUpdatingAchievementList;

        private void GetAchievements()
        {
            var textSearch = this._MatchingStringTextBox.Text.Length > 0
                ? this._MatchingStringTextBox.Text
                : null;

            this._IsUpdatingAchievementList = true;

            this._AchievementListView.Items.Clear();
            this._AchievementListView.BeginUpdate();

            bool wantLocked = this._DisplayLockedOnlyButton.Checked == true;
            bool wantUnlocked = this._DisplayUnlockedOnlyButton.Checked == true;

            foreach (var def in this._AchievementDefinitions)
            {
                if (string.IsNullOrEmpty(def.Id) == true)
                {
                    continue;
                }

                if (this._SteamClient.SteamUserStats.GetAchievementAndUnlockTime(
                    def.Id,
                    out bool isAchieved,
                    out var unlockTime) == false)
                {
                    continue;
                }

                bool wanted = (wantLocked == false && wantUnlocked == false) || isAchieved switch
                {
                    true => wantUnlocked,
                    false => wantLocked,
                };
                if (wanted == false)
                {
                    continue;
                }

                if (textSearch != null)
                {
                    if (def.Name.IndexOf(textSearch, StringComparison.OrdinalIgnoreCase) < 0 &&
                        def.Description.IndexOf(textSearch, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                }

                Stats.AchievementInfo info = new()
                {
                    Id = def.Id,
                    IsAchieved = isAchieved,
                    UnlockTime = isAchieved == true && unlockTime > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(unlockTime).LocalDateTime
                        : null,
                    IconNormal = string.IsNullOrEmpty(def.IconNormal) ? null : def.IconNormal,
                    IconLocked = string.IsNullOrEmpty(def.IconLocked) ? null : def.IconLocked,
                    Name = def.Name,
                    Description = def.Description,
                    Permission = def.Permission,
                };

                var item = new ListViewItem()
                {
                    Checked = isAchieved,
                    Text = def.Name,
                    BackColor = isAchieved == true ? Color.Black : Color.FromArgb(64, 0, 0),
                };

                info.Item = item;

                // [1] Order — empty initially
                item.SubItems.Add("");

                // [2] Description
                if (item.Text.StartsWith("#", StringComparison.InvariantCulture) == true)
                {
                    item.Text = info.Id;
                    item.SubItems.Add("");
                }
                else
                {
                    item.SubItems.Add(info.Description);
                }

                // [3] Delay (min) — empty initially
                item.SubItems.Add("");

                // [4] Unlock Time
                item.SubItems.Add(info.UnlockTime.HasValue == true
                    ? info.UnlockTime.Value.ToString()
                    : "");

                // [5] % Global — from cache if available, else "..." until polling fills it
                item.SubItems.Add(this._GlobalPercentCache.TryGetValue(def.Id, out float cachedPct)
                    ? cachedPct.ToString("0.0") + "%"
                    : "...");

                item.Tag = info;
                info.ImageIndex = 0;

                this.AddAchievementToIconQueue(info, false);
                this._AchievementListView.Items.Add(item);
            }

            this._AchievementListView.EndUpdate();
            this._IsUpdatingAchievementList = false;

            this.DownloadNextIcon();
        }

        private void GetStatistics()
        {
            this._Statistics.Clear();
            foreach (var stat in this._StatDefinitions)
            {
                if (string.IsNullOrEmpty(stat.Id) == true)
                {
                    continue;
                }

                if (stat is Stats.IntegerStatDefinition intStat)
                {
                    if (this._SteamClient.SteamUserStats.GetStatValue(intStat.Id, out int value) == false)
                    {
                        continue;
                    }
                    this._Statistics.Add(new Stats.IntStatInfo()
                    {
                        Id = intStat.Id,
                        DisplayName = intStat.DisplayName,
                        IntValue = value,
                        OriginalValue = value,
                        IsIncrementOnly = intStat.IncrementOnly,
                        Permission = intStat.Permission,
                    });
                }
                else if (stat is Stats.FloatStatDefinition floatStat)
                {
                    if (this._SteamClient.SteamUserStats.GetStatValue(floatStat.Id, out float value) == false)
                    {
                        continue;
                    }
                    this._Statistics.Add(new Stats.FloatStatInfo()
                    {
                        Id = floatStat.Id,
                        DisplayName = floatStat.DisplayName,
                        FloatValue = value,
                        OriginalValue = value,
                        IsIncrementOnly = floatStat.IncrementOnly,
                        Permission = floatStat.Permission,
                    });
                }
            }
        }

        private void AddAchievementToIconQueue(Stats.AchievementInfo info, bool startDownload)
        {
            int imageIndex = this._AchievementImageList.Images.IndexOfKey(
                info.IsAchieved == true ? info.IconNormal : info.IconLocked);

            if (imageIndex >= 0)
            {
                info.ImageIndex = imageIndex;
            }
            else
            {
                this._IconQueue.Add(info);

                if (startDownload == true)
                {
                    this.DownloadNextIcon();
                }
            }
        }

        private int StoreAchievements()
        {
            if (this._AchievementListView.Items.Count == 0)
            {
                return 0;
            }

            List<Stats.AchievementInfo> achievements = new();
            foreach (ListViewItem item in this._AchievementListView.Items)
            {
                if (item.Tag is not Stats.AchievementInfo achievementInfo ||
                    achievementInfo.IsAchieved == item.Checked)
                {
                    continue;
                }

                achievementInfo.IsAchieved = item.Checked;
                achievements.Add(achievementInfo);
            }

            if (achievements.Count == 0)
            {
                return 0;
            }

            foreach (var info in achievements)
            {
                if (this._SteamClient.SteamUserStats.SetAchievement(info.Id, info.IsAchieved) == false)
                {
                    MessageBox.Show(
                        this,
                        $"An error occurred while setting the state for {info.Id}, aborting store.",
                        "Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return -1;
                }
            }

            return achievements.Count;
        }

        private int StoreStatistics()
        {
            if (this._Statistics.Count == 0)
            {
                return 0;
            }

            var statistics = this._Statistics.Where(stat => stat.IsModified == true).ToList();
            if (statistics.Count == 0)
            {
                return 0;
            }

            foreach (var stat in statistics)
            {
                if (stat is Stats.IntStatInfo intStat)
                {
                    if (this._SteamClient.SteamUserStats.SetStatValue(
                        intStat.Id,
                        intStat.IntValue) == false)
                    {
                        MessageBox.Show(
                            this,
                            $"An error occurred while setting the value for {stat.Id}, aborting store.",
                            "Error",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        return -1;
                    }
                }
                else if (stat is Stats.FloatStatInfo floatStat)
                {
                    if (this._SteamClient.SteamUserStats.SetStatValue(
                        floatStat.Id,
                        floatStat.FloatValue) == false)
                    {
                        MessageBox.Show(
                            this,
                            $"An error occurred while setting the value for {stat.Id}, aborting store.",
                            "Error",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        return -1;
                    }
                }
                else
                {
                    throw new InvalidOperationException("unsupported stat type");
                }
            }

            return statistics.Count;
        }

        private void DisableInput()
        {
            this._ReloadButton.Enabled = false;
            this._StoreButton.Enabled = false;
        }

        private void EnableInput()
        {
            this._ReloadButton.Enabled = true;
            this._StoreButton.Enabled = true;
        }

        private void OnTimer(object sender, EventArgs e)
        {
            this._CallbackTimer.Enabled = false;
            this._SteamClient.RunCallbacks(false);
            this._CallbackTimer.Enabled = true;
        }

        private void OnFormResize(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                this.Hide();
                this._TrayIcon.Visible = true;
            }
        }

        private void OnTrayMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (this._ScheduleQueue != null && this._ScheduleIndex < this._ScheduleQueue.Count)
            {
                var remaining = this._NextUnlockTime - DateTime.Now;
                if (remaining.TotalSeconds < 0) remaining = TimeSpan.Zero;
                var nextName = this._ScheduleQueue[this._ScheduleIndex].Info.Name;
                int total = this._ScheduleQueue.Count;
                this._TrayMenuStatus.Text =
                    $"[{this._ScheduleIndex + 1}/{total}] \"{nextName}\" — {(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";
            }
            else
            {
                this._TrayMenuStatus.Text = "No schedule running";
            }
        }

        private void OnTrayRestore(object sender, EventArgs e)
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate();
            this._TrayIcon.Visible = false;
        }

        private void OnTrayExit(object sender, EventArgs e)
        {
            this._TrayIcon.Visible = false;
            Application.Exit();
        }

        private void OnRefresh(object sender, EventArgs e)
        {
            this.RefreshStats();
        }

        private void OnLockAll(object sender, EventArgs e)
        {
            foreach (ListViewItem item in this._AchievementListView.Items)
            {
                item.Checked = false;
            }
        }

        private void OnInvertAll(object sender, EventArgs e)
        {
            foreach (ListViewItem item in this._AchievementListView.Items)
            {
                item.Checked = !item.Checked;
            }
        }

        private void OnUnlockAll(object sender, EventArgs e)
        {
            foreach (ListViewItem item in this._AchievementListView.Items)
            {
                item.Checked = true;
            }
        }

        private bool Store()
        {
            if (this._SteamClient.SteamUserStats.StoreStats() == false)
            {
                MessageBox.Show(
                    this,
                    "An error occurred while storing, aborting.",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }

            return true;
        }

        private void OnStore(object sender, EventArgs e)
        {
            int achievements = this.StoreAchievements();
            if (achievements < 0)
            {
                this.RefreshStats();
                return;
            }

            int stats = this.StoreStatistics();
            if (stats < 0)
            {
                this.RefreshStats();
                return;
            }

            if (this.Store() == false)
            {
                this.RefreshStats();
                return;
            }

            MessageBox.Show(
                this,
                $"Stored {achievements} achievements and {stats} statistics.",
                "Information",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            this.RefreshStats();
        }

        private void OnStatDataError(object sender, DataGridViewDataErrorEventArgs e)
        {
            if (e.Context != DataGridViewDataErrorContexts.Commit)
            {
                return;
            }

            var view = (DataGridView)sender;
            if (e.Exception is Stats.StatIsProtectedException)
            {
                e.ThrowException = false;
                e.Cancel = true;
                view.Rows[e.RowIndex].ErrorText = "Stat is protected! -- you can't modify it";
            }
            else
            {
                e.ThrowException = false;
                e.Cancel = true;
                view.Rows[e.RowIndex].ErrorText = "Invalid value";
            }
        }

        private void OnStatAgreementChecked(object sender, EventArgs e)
        {
            this._StatisticsDataGridView.Columns[1].ReadOnly = this._EnableStatsEditingCheckBox.Checked == false;
        }

        private void OnStatCellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            var view = (DataGridView)sender;
            view.Rows[e.RowIndex].ErrorText = "";
        }

        private void OnResetAllStats(object sender, EventArgs e)
        {
            if (MessageBox.Show(
                "Are you absolutely sure you want to reset stats?",
                "Warning",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) == DialogResult.No)
            {
                return;
            }

            bool achievementsToo = DialogResult.Yes == MessageBox.Show(
                "Do you want to reset achievements too?",
                "Question",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (MessageBox.Show(
                "Really really sure?",
                "Warning",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Error) == DialogResult.No)
            {
                return;
            }

            if (this._SteamClient.SteamUserStats.ResetAllStats(achievementsToo) == false)
            {
                MessageBox.Show(this, "Failed.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            this.RefreshStats();
        }

        private void OnCheckAchievement(object sender, ItemCheckEventArgs e)
        {
            if (sender != this._AchievementListView)
            {
                return;
            }

            if (this._IsUpdatingAchievementList == true)
            {
                return;
            }

            if (this._AchievementListView.Items[e.Index].Tag is not Stats.AchievementInfo info)
            {
                return;
            }

            if ((info.Permission & 3) != 0)
            {
                MessageBox.Show(
                    this,
                    "Sorry, but this is a protected achievement and cannot be managed with Steam Achievement Manager.",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                e.NewValue = e.CurrentValue;
                return;
            }

            // --- Auto-assign schedule order ---
            if (e.NewValue == CheckState.Checked)
            {
                info.ScheduleOrder = ++this._ScheduleOrderCounter;
                this._AchievementListView.Items[e.Index].SubItems[1].Text =
                    info.ScheduleOrder.Value.ToString();
                this._AchievementListView.Items[e.Index].BackColor = Color.FromArgb(0, 72, 0);
            }
            else
            {
                // Remove order and renumber remaining
                info.ScheduleOrder = null;
                this._AchievementListView.Items[e.Index].SubItems[1].Text = "";
                this._AchievementListView.Items[e.Index].BackColor =
                    info.IsAchieved ? Color.Black : Color.FromArgb(64, 0, 0);

                var remaining = new List<Stats.AchievementInfo>();
                foreach (ListViewItem item in this._AchievementListView.Items)
                {
                    if (item.Tag is Stats.AchievementInfo i && i.ScheduleOrder.HasValue)
                        remaining.Add(i);
                }
                remaining.Sort((a, b) => a.ScheduleOrder!.Value.CompareTo(b.ScheduleOrder!.Value));
                this._ScheduleOrderCounter = 0;
                foreach (var i in remaining)
                {
                    i.ScheduleOrder = ++this._ScheduleOrderCounter;
                    if (i.Item != null)
                        i.Item.SubItems[1].Text = i.ScheduleOrder.Value.ToString();
                }
            }
        }

        private void OnDisplayUncheckedOnly(object sender, EventArgs e)
        {
            if ((sender as ToolStripButton).Checked == true)
            {
                this._DisplayLockedOnlyButton.Checked = false;
            }

            this.GetAchievements();
        }

        private void OnDisplayCheckedOnly(object sender, EventArgs e)
        {
            if ((sender as ToolStripButton).Checked == true)
            {
                this._DisplayUnlockedOnlyButton.Checked = false;
            }

            this.GetAchievements();
        }

        private void OnFilterUpdate(object sender, KeyEventArgs e)
        {
            this.GetAchievements();
        }

        // -------------------------------------------------------
        // Column header sorting
        // -------------------------------------------------------

        private void OnAchievementColumnClick(object sender, ColumnClickEventArgs e)
        {
            // Only sort Name (0), Unlock Time (4), % Global (5)
            if (e.Column != 0 && e.Column != 4 && e.Column != 5)
                return;

            if (e.Column == this._SortColumn)
            {
                this._SortOrder = this._SortOrder == SortOrder.Ascending
                    ? SortOrder.Descending
                    : SortOrder.Ascending;
            }
            else
            {
                this._SortColumn = e.Column;
                this._SortOrder = SortOrder.Ascending;
            }

            this._AchievementListView.ListViewItemSorter =
                new AchievementListComparer(this._SortColumn, this._SortOrder);
            this._AchievementListView.Sort();
        }

        private sealed class AchievementListComparer : System.Collections.IComparer
        {
            private readonly int _Column;
            private readonly SortOrder _Order;

            public AchievementListComparer(int column, SortOrder order)
            {
                this._Column = column;
                this._Order = order;
            }

            public int Compare(object x, object y)
            {
                var ix = (ListViewItem)x;
                var iy = (ListViewItem)y;

                string tx = ix.SubItems.Count > this._Column ? ix.SubItems[this._Column].Text : "";
                string ty = iy.SubItems.Count > this._Column ? iy.SubItems[this._Column].Text : "";

                int result;

                if (this._Column == 5) // % Global — numeric
                {
                    float fx = float.TryParse(tx.TrimEnd('%'), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out float fxv) ? fxv : -1f;
                    float fy = float.TryParse(ty.TrimEnd('%'), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out float fyv) ? fyv : -1f;
                    result = fx.CompareTo(fy);
                }
                else if (this._Column == 4) // Unlock Time — date, empty goes to end
                {
                    bool hx = DateTime.TryParse(tx, out DateTime dtx);
                    bool hy = DateTime.TryParse(ty, out DateTime dty);
                    if (!hx && !hy) result = 0;
                    else if (!hx) result = 1;
                    else if (!hy) result = -1;
                    else result = dtx.CompareTo(dty);
                }
                else // Name — case-insensitive string
                {
                    result = string.Compare(tx, ty, StringComparison.OrdinalIgnoreCase);
                }

                return this._Order == SortOrder.Descending ? -result : result;
            }
        }

        // -------------------------------------------------------
        // Inline delay editor (column 4 = Delay (min))
        // -------------------------------------------------------

        private void OnAchievementListViewMouseClick(object sender, MouseEventArgs e)
        {
            var hit = this._AchievementListView.HitTest(e.X, e.Y);
            if (hit.Item == null || hit.SubItem == null)
                return;

            int subItemIndex = hit.Item.SubItems.IndexOf(hit.SubItem);
            if (subItemIndex != 3) // Only Delay column is editable
                return;

            this._EditingItem = hit.Item;
            this._EditingSubItemIndex = subItemIndex;

            var bounds = hit.SubItem.Bounds;
            this._InlineEditBox.SetBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            this._InlineEditBox.Text = hit.SubItem.Text;
            this._InlineEditBox.Visible = true;
            this._InlineEditBox.Focus();
            this._InlineEditBox.SelectAll();
        }

        private void CommitInlineEdit()
        {
            if (this._EditingItem == null)
                return;

            if (this._EditingItem.Tag is Stats.AchievementInfo info)
            {
                string text = this._InlineEditBox.Text.Trim();
                if (int.TryParse(text, out int delay) && delay > 0)
                {
                    info.DelayMinutes = delay;
                    this._EditingItem.SubItems[3].Text = delay.ToString();
                }
                else
                {
                    info.DelayMinutes = 0;
                    this._EditingItem.SubItems[3].Text = "";
                }
            }

            this._InlineEditBox.Visible = false;
            this._EditingItem = null;
        }

        private void InlineEditBox_LostFocus(object sender, EventArgs e)
        {
            this.CommitInlineEdit();
        }

        private void InlineEditBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Return)
            {
                this.CommitInlineEdit();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                this._InlineEditBox.Visible = false;
                this._EditingItem = null;
                e.Handled = true;
            }
        }

        // -------------------------------------------------------
        // Schedule: Run button + Timer tick
        // -------------------------------------------------------

        private void OnRunSchedule(object sender, EventArgs e)
        {
            var entries = new List<(Stats.AchievementInfo Info, int DelayMs)>();
            foreach (ListViewItem item in this._AchievementListView.Items)
            {
                if (item.Tag is not Stats.AchievementInfo info)
                    continue;
                if (info.ScheduleOrder == null)
                    continue;

                // Base delay + random 0–59 seconds so it never fires at exactly round minute
                int baseMs = info.DelayMinutes * 60 * 1000;
                int randomMs = info.DelayMinutes > 0 ? this._Random.Next(0, 60) * 1000 : 0;
                entries.Add((info, baseMs + randomMs));
            }

            if (entries.Count == 0)
            {
                MessageBox.Show(this,
                    "No achievements scheduled.\nCheck achievements to add them to the schedule.",
                    "Schedule", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            entries.Sort((a, b) => a.Info.ScheduleOrder!.Value.CompareTo(b.Info.ScheduleOrder!.Value));

            this._ScheduleQueue = entries;
            this._ScheduleIndex = 0;
            this._RunScheduleButton.Enabled = false;
            this._StopScheduleButton.Enabled = true;
            this._RunScheduleButton.Text = "Running...";

            int firstDelay = this._ScheduleQueue[0].DelayMs;
            this._ScheduleTimer.Interval = firstDelay > 0 ? firstDelay : 1000;
            this._NextUnlockTime = DateTime.Now.AddMilliseconds(this._ScheduleTimer.Interval);
            this._ScheduleTimer.Start();
            this._CountdownTimer.Start();
        }

        private void OnStopSchedule(object sender, EventArgs e)
        {
            this._ScheduleTimer.Stop();
            this._CountdownTimer.Stop();
            this._ScheduleQueue = null;
            this._RunScheduleButton.Enabled = true;
            this._RunScheduleButton.Text = "Run Schedule";
            this._StopScheduleButton.Enabled = false;
            this._GameStatusLabel.Text = "Schedule stopped.";
        }

        private void OnCountdownTick(object sender, EventArgs e)
        {
            if (this._ScheduleQueue == null || this._ScheduleIndex >= this._ScheduleQueue.Count)
            {
                this._CountdownTimer.Stop();
                return;
            }

            var remaining = this._NextUnlockTime - DateTime.Now;
            if (remaining.TotalSeconds < 0)
                remaining = TimeSpan.Zero;

            var nextName = this._ScheduleQueue[this._ScheduleIndex].Info.Name;
            int totalQueued = this._ScheduleQueue.Count;
            string statusText = $"[{this._ScheduleIndex + 1}/{totalQueued}] Next: \"{nextName}\" — {(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";

            this._GameStatusLabel.Text = statusText;
        }

        private void OnScheduleTick(object sender, EventArgs e)
        {
            this._ScheduleTimer.Stop();

            if (this._ScheduleQueue == null || this._ScheduleIndex >= this._ScheduleQueue.Count)
            {
                this._CountdownTimer.Stop();
                this._RunScheduleButton.Enabled = true;
                this._RunScheduleButton.Text = "Run Schedule";
                this._StopScheduleButton.Enabled = false;
                return;
            }

            var entry = this._ScheduleQueue[this._ScheduleIndex];

            // Unlock in Steam
            entry.Info.IsAchieved = true;
            this._SteamClient.SteamUserStats.SetAchievement(entry.Info.Id, true);
            this._SteamClient.SteamUserStats.StoreStats();

            // Update row in ListView
            if (entry.Info.Item != null)
            {
                this._IsUpdatingAchievementList = true;
                entry.Info.Item.Checked = true;
                entry.Info.Item.BackColor = Color.Black;
                this._IsUpdatingAchievementList = false;
            }

            this._ScheduleIndex++;

            if (this._ScheduleIndex < this._ScheduleQueue.Count)
            {
                var next = this._ScheduleQueue[this._ScheduleIndex];
                int nextDelay = next.DelayMs > 0 ? next.DelayMs : 1000;
                this._ScheduleTimer.Interval = nextDelay;
                this._NextUnlockTime = DateTime.Now.AddMilliseconds(nextDelay);
                this._ScheduleTimer.Start();
            }
            else
            {
                this._CountdownTimer.Stop();
                this._RunScheduleButton.Enabled = true;
                this._RunScheduleButton.Text = "Run Schedule";
                this._StopScheduleButton.Enabled = false;
                this._GameStatusLabel.Text = "Schedule complete! All achievements unlocked.";
                MessageBox.Show(this, "All scheduled achievements unlocked!", "Done",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}
