using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Web;
using HunterPie.Core;
using HunterPie.Core.Events;
using Newtonsoft.Json;

namespace HunterPie.Plugins {
    public struct Response {
        public Status status { get; set; }
        public string value { get; set; }

        public static Response Create(string json) {
            return JsonConvert.DeserializeObject<Response>(json);
        }
    }

    public enum Status {
        ok = 0,
        sessionDoesNotExist = 1,
        sessionAlreadyExists = 2,
        monsterOutsideRange = 3,
        partOutsideRange = 4,
        ailmentOutsideRange = 5,
        exception = 10,
        e404 = 404
    }

    public class StatusList {
        private readonly Dictionary<Status, int> list;

        public StatusList(int a = 0) {
            list = new Dictionary<Status, int>();
            foreach (Status s in Enum.GetValues(typeof(Status))) {
                list.Add(s, 0);
            }
        }

        public int errorCount {
            get {
                int count = 0;
                foreach (Status s in Enum.GetValues(typeof(Status))) {
                    count += list[s];
                }
                count -= list[Status.ok]; //Status.ok is not an error, so it shouldn't be included
                return count;
            }
        }

        public int count(Status status) {
            return list[status];
        }

        public void clear() { //set all status counts to 0
            foreach (Status s in Enum.GetValues(typeof(Status))) {
                list[s] = 0;
            }
        }

        public override string ToString() {
            string str = "";
            foreach (Status s in Enum.GetValues(typeof(Status))) {
                if (list[s] > 0) {
                    str += s + " - " + list[s] + "\n";
                }
            }
            return str;
        }

        public void increment(Status status) {
            list[status]++;
        }
    }

    internal class Config {
        public bool showDetailedMessages { get; set; } = false;
        public int delay { get; set; } = 1000;
    }

    public class SyncPlugin : IPlugin {
        private const string ServerUrl = "http://mhwsync.herokuapp.com";
        private const int ApiVersion = 2;
        private string configPath = "";
        private int retries = 5;
        private string _sessionID = "";
        private string _partyLeader = "";

        private Thread syncThreadReference;
        private bool terminateSyncThread = false;
        private StreamWriter errorLogWriter = null;
        private Config config;
        private readonly DateTime[] lastHpUpdate = new DateTime[3];

        private readonly StatusList statusList = new StatusList();

        public Game Context { get; set; }
        public string Description { get; set; }
        public string Name { get; set; }
        private bool isInParty { get; set; } = false;

        private bool isPartyLeader {
            get {
                return Context.Player.PlayerParty.IsLocalHost;
            }
        }

        private string partyLeader {
            get {
                return _partyLeader;
            }
            set {
                _partyLeader = HttpUtility.UrlEncode(value);
            }
        }

        private string sessionID {
            get {
                return _sessionID;
            }
            set {
                _sessionID = HttpUtility.UrlEncode(value);
            }
        }

        private string sessionUrlString {
            get {
                return ServerUrl + "/session/" + sessionID + partyLeader;
            }
        }

        private void readConfig() {
            string serializedFile = File.ReadAllText(configPath);
            if (string.IsNullOrEmpty(serializedFile)) {
                createConfig();
            } else {
                config = JsonConvert.DeserializeObject<Config>(serializedFile);
                log("Loaded config file");

                //save config to add missing values
                saveConfig();
            }
        }

        private void saveConfig() {
            File.WriteAllText(configPath, JsonConvert.SerializeObject(config, Formatting.Indented));
        }

        private void createConfig() {
            config = new Config();
            saveConfig();
        }

        public void Initialize(Game context) {
            Name = "SyncPlugin";
            Description = "Part and ailment synchronization for HunterPie";

            configPath = ".\\Modules\\" + Name + "\\" + Name + ".json";
            if (File.Exists(configPath)) {
                readConfig();
            } else {
                createConfig();
            }

            while (!isServerAlive()) {
                if (retries-- > 0) {
                    log("Could not reach server, " + retries + " retries remaining");
                    Thread.Sleep(500);
                } else {
                    log("Stopping module initialization");
                    return;
                }
            }

            if (!hasCorrectApiVersion()) {
                return;
            }

            Context = context;

            Context.Player.OnSessionChange += OnSessionChange;
            Context.Player.OnCharacterLogout += OnCharacterLogout;
            Context.Player.OnZoneChange += OnZoneChange;

            for (int i = 0; i < Context.Monsters.Length; i++) {
                Context.Monsters[i].OnMonsterDespawn += OnMonsterDespawn;
                Context.Monsters[i].OnMonsterDeath += OnMonsterDeath;
                Context.Monsters[i].OnHPUpdate += OnHPUpdate;
            }

            try {
                errorLogWriter = new StreamWriter(File.Open("Modules\\" + Name + "\\errors.log", FileMode.Append, FileAccess.Write, FileShare.Read));
                errorLogWriter.AutoFlush = true;
            } catch (Exception e) {
                log("Error opening/creating error log: " + e.Message);
                errorLogWriter = null;
            }

            InitializeSession();
            syncThreadReference = new Thread(syncThread);
        }

        public void Unload() {
            Context.Player.OnSessionChange -= OnSessionChange;
            Context.Player.OnCharacterLogout -= OnCharacterLogout;
            Context.Player.OnZoneChange -= OnZoneChange;

            for (int i = 0; i < Context.Monsters.Length; i++) {
                Context.Monsters[i].OnMonsterDespawn -= OnMonsterDespawn;
                Context.Monsters[i].OnMonsterDeath -= OnMonsterDeath;
                Context.Monsters[i].OnHPUpdate -= OnHPUpdate;
            }

            if (isInParty) {
                quitSession();
                if (syncThreadReference.IsAlive) {
                    stopSyncThread();
                }
            }

            if (errorLogWriter != null) {
                errorLogWriter.Flush();
                errorLogWriter.Close();
            }
        }

        private bool hasCorrectApiVersion() {
            Response r = get(ServerUrl + "/version");
            if (r.status == Status.ok) {
                if (int.Parse(r.value) == ApiVersion) {
                    return true;
                } else {
                    error("API version mismatch - The server is incompatible with your version of the plugin, this issue should be resolved soon, please try again later.");
                    return false;
                }
            } else {
                error("Error checking API version: " + r.status + " - " + r.value);
                return false;
            }
        }

        private void clearMonster(int monsterIndex) {
            get(sessionUrlString + "/monster/" + monsterIndex + "/clear");
        }

        private bool createPartyIfNotExist() {
            if (partyExists()) {
                log("Did not create session because it already exists", true);
                return true;
            }

            Response r = get(sessionUrlString + "/create");
            if (r.status == Status.ok) {
                log("Created session " + sessionID + partyLeader, true);
                return true;
            }
            if (r.status == Status.sessionAlreadyExists) {
                log("Did not create session because it already exists", true);
                return true;
            }

            error("Error creating session: " + r.status + " - " + r.value);
            return false;
        }

        private Response get(string url) {
            try {
                WebRequest request = WebRequest.Create(Uri.EscapeUriString(url));
                WebResponse response = request.GetResponse();
                Stream stream = response.GetResponseStream();
                StreamReader reader = new StreamReader(stream);
                string str = reader.ReadToEnd();
                reader.Close();
                response.Close();
                Response r = Response.Create(str);
                if (!url.Contains("/exists") && r.status != Status.sessionDoesNotExist) {
                    statusList.increment(r.status);
                }
                return r;
            } catch (Exception e) {
                error(e);
                statusList.increment(Status.exception);
                Response response = new Response {
                    status = Status.exception,
                    value = "Exception in SyncPlugin.get"
                };
                return response;
            }
        }

        private bool isServerAlive() {
            if (get(ServerUrl).status == Status.ok) {
                return true;
            }
            return false;
        }

        private void log(string message, bool detailedOnly = false) {
            if (detailedOnly) {
                if (config.showDetailedMessages) {
                    HunterPie.Logger.Debugger.Module(message, Name);
                }
            } else {
                HunterPie.Logger.Debugger.Module(message, Name);
            }
        }

        private void error(string message) {
            log(message);
            if (errorLogWriter != null) {
                try {
                    errorLogWriter.WriteLine("[" + DateTime.UtcNow.ToString(new CultureInfo("en-gb")) + "] " + message);
                } catch (ObjectDisposedException) {
                    return;
                } catch (Exception e) {
                    log(e.GetType() + " in error(): " + e.Message);
                }
            }
        }

        private void error(Exception e, [System.Runtime.CompilerServices.CallerMemberName] string functionName = "", [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = 0) {
            if (functionName == "") {
                error(e.GetType() + ": " + e.Message);
            } else {
                error(e.GetType() + " in " + functionName + ", line " + sourceLineNumber + ": " + e.Message);
            }
        }

        #region eventhandlers

        private void OnHPUpdate(object source, MonsterUpdateEventArgs args) {
            lastHpUpdate[((Monster)source).MonsterNumber - 1] = DateTime.Now;
        }

        private void OnCharacterLogout(object source, EventArgs args) {
            if (isInParty) {
                quitSession();
            }
        }

        private void OnMonsterDeath(object source, EventArgs args) {
            if (isInParty && isPartyLeader) {
                clearMonster(((Monster)source).MonsterNumber - 1);
            }
        }

        private void OnMonsterDespawn(object source, EventArgs args) {
            if (isInParty && isPartyLeader) {
                clearMonster(((Monster)source).MonsterNumber - 1);
            }
        }

        private void OnSessionChange(object source, EventArgs args) {
            if (isInParty) { //quit old session
                quitSession();
            }
            InitializeSession();
        }

        private void OnZoneChange(object source, EventArgs args) {
            if (isInParty) { //quit old session
                quitSession();
            }
            InitializeSession();
        }

        #endregion eventhandlers

        private async void InitializeSession() {
            try {
                await System.Threading.Tasks.Task.Yield();
                Thread.Sleep(1000);
                sessionID = Context.Player.SessionID;
            
                if (Context.Player.InPeaceZone || Context.Player.ZoneID == 504 || string.IsNullOrEmpty(sessionID)) { //if player is in peace zone/training area or sessionID is empty
                    if (isInParty) {
                        quitSession();
                        log("Quitting session", true);
                    }
                    return;
                }

                if (isPartyLeader) {
                    partyLeader = Context.Player.Name;
                    isInParty = createPartyIfNotExist();
                    if (isInParty) {
                        startSyncThread();
                    }
                } else {
                    for (int i = 0; i < Context.Player.PlayerParty.Members.Count; i++) {
                        if (Context.Player.PlayerParty.Members[i].IsPartyLeader) {
                            partyLeader = Context.Player.PlayerParty.Members[i].Name;
                            break;
                        }
                    }
                    isInParty = partyExists();
                    if (isInParty) { //if party leader has SyncPlugin installed and enabled
                        log("Entered " + partyLeader + "\'s session");
                        startSyncThread();
                    } else {
                        log("There is no session to enter", true);
                    }
                }
            } catch (NullReferenceException e) { //happened while closing the game, needs further testing
                if (!string.IsNullOrEmpty(sessionID)) {
                    error(e);
                }
            }
        }

        private bool partyExists() {
            if (string.IsNullOrEmpty(sessionUrlString) || string.IsNullOrEmpty(partyLeader) || string.IsNullOrEmpty(sessionID)) {
                return false;
            }
            if (get(sessionUrlString + "/exists").status == Status.ok) {
                return true;
            }
            return false;
        }

        private void pullParts() {
            try {
                Response result;
                for (int i = 0; i < 3; i++) {
                    for (int j = 0; j < Context.Monsters[i].Parts.Count; j++) {
                        result = get(sessionUrlString + "/monster/" + i + "/part/" + j + "/current_hp");
                        if (result.status == Status.ok) {
                            Context.Monsters[i].Parts[j].Health = int.Parse(result.value);
                        } else {
                            if (statusList.count(result.status) == 1) { //only show first error of each type; status count has already been incremented if error has occurred
                                error("Error in pullParts: " + result.value);
                            }
                        }
                    }
                }
            } catch (ArgumentOutOfRangeException e) {
                if (!string.IsNullOrEmpty(sessionID)) {
                    error(e);
                }
            }
        }

        private void pullAilments() {
            try {
                Response result;
                for (int i = 0; i < 3; i++) {
                    for (int j = 0; j < Context.Monsters[i].Ailments.Count; j++) {
                        result = get(sessionUrlString + "/monster/" + (Context.Monsters[i].MonsterNumber - 1) + "/ailment/" + j + "/current_buildup");
                        if (result.status == Status.ok) {
                            Context.Monsters[i].Ailments[j].Buildup = int.Parse(result.value);
                        } else {
                            if (statusList.count(result.status) == 1) { //only show first error of each type; status count has already been incremented if error has occurred
                                error("Error in pullAilments: " + result.value);
                            }
                        }
                    }
                }
            } catch (ArgumentOutOfRangeException e) {
                if (!string.IsNullOrEmpty(sessionID)) {
                    error(e);
                }
            }
        }

        private void pushAilments() {
            Response result;
            int value;

            for (int i = 0; i < 3; i++) {
                for (int j = 0; j < Context.Monsters[i].Ailments.Count; j++) {
                    if (!isInParty) { //quitSession() could have been called while running this loop
                        return;
                    }
                    if (Context.Monsters[i].Ailments[j].Buildup == float.NaN || (int)Context.Monsters[i].Ailments[j].Buildup < 0) {
                        value = 0;
                    } else {
                        value = (int)Context.Monsters[i].Ailments[j].Buildup;
                    }
                    result = get(sessionUrlString + "/monster/" + i + "/ailment/" + j + "/current_buildup/" + value);
                    if (result.status != Status.ok) {
                        if (statusList.count(result.status) == 1) { //only show first error of each type; status count has already been incremented if error has occurred
                            error("Error in pushAilments: " + result.value);
                        }
                    }
                }
            }
        }

        private void pushParts(Monster monster) {
            Response result;
            int value;

            for (int i = 0; i < monster.Parts.Count; i++) {
                if (!isInParty) { //quitSession() could have been called while running this loop
                    return;
                }
                if (monster.Parts[i].Health == float.NaN || (int)monster.Parts[i].Health < 0) {
                    value = 0;
                } else {
                    value = (int)monster.Parts[i].Health;
                }

                result = get(sessionUrlString + "/monster/" + (monster.MonsterNumber - 1) + "/part/" + i + "/current_hp/" + value);
                if (result.status != Status.ok) {
                    if (statusList.count(result.status) == 1) { //only show first error of each type; status count has already been incremented if error has occurred
                        error("Error " + result.status + " in pushParts: " + result.value);
                    }
                    return;
                }
            }
        }

        private void quitSession() {
            if (isPartyLeader) {
                get(sessionUrlString + "/delete");
            }
            if (statusList.errorCount > 0) {
                error(statusList.errorCount + " errors occurred in this session");
                error(statusList.ToString());
                statusList.clear();
            } else {
                log("No errors occurred in this session", true);
            }
            if (isInParty && !isPartyLeader) {
                log("Left session");
            } else if (isInParty && isPartyLeader) {
                log("Deleted session", true);
            }
            partyLeader = "";
            isInParty = false;
        }

        private void startSyncThread() {
            if (syncThreadReference.IsAlive) {
                return;
            }
            try {
                syncThreadReference.Start();
            } catch (Exception e) {
                error(e);
            }
        }

        private void stopSyncThread() {
            terminateSyncThread = true;           
        }

        private void syncThread() {
            try {
                while (Context.IsActive && !terminateSyncThread) {
                    if (isInParty) {
                        if (isPartyLeader) {
                            for (int i = 0; i < 3; i++) {
                                if ((DateTime.Now - lastHpUpdate[i]).TotalSeconds < 3) { //only push new part data if monster hp have changed in the last 3 seconds
                                    pushParts(Context.Monsters[i]);
                                }
                            }

                            pushAilments();
                        } else {
                            pullParts();
                            pullAilments();
                        }
                    }
                    Thread.Sleep(config.delay);
                }
            } catch (ThreadAbortException e) {
                error(e);
            }
        }
    }
}