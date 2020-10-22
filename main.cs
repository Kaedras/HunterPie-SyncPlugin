using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Web;
using HunterPie.Core;
using HunterPie.Core.Events;
using HunterPie.Logger;
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

    public class SyncPlugin : IPlugin {
        private const string ServerUrl = "http://mhwsync.herokuapp.com";
        private string partyLeader = "";
        private int retries = 5;
        private int delay;
        private string sessionID = "";
        private string sessionUrlString = "";
        private Thread syncThreadReference;
        private bool showDetailedMessages = false;
        private StreamWriter errorLogWriter = null;

        private readonly StatusList statusList = new StatusList();

        public Game Context { get; set; }
        public string Description { get; set; }
        public string Name { get; set; }
        private bool isInParty { get; set; } = false;
        private bool isPartyLeader { get; set; } = false;
        private bool monsterThreadsStopped { get; set; } = false;

        private string PartyLeader {
            get {
                return partyLeader;
            }
            set {
                partyLeader = HttpUtility.UrlEncode(value);
                sessionUrlString = ServerUrl + "/session/" + SessionID + PartyLeader;
            }
        }

        private string SessionID {
            get {
                return sessionID;
            }
            set {
                sessionID = HttpUtility.UrlEncode(value);
                sessionUrlString = ServerUrl + "/session/" + SessionID + PartyLeader;
            }
        }

        public void Initialize(Game context) {
            Name = "SyncPlugin";
            Description = "Part and ailment synchronization for HunterPie";

            while (!isServerAlive()) {
                if (retries-- > 0) {
                    log("Could not reach server, " + retries + " retries remaining");
                    Thread.Sleep(500);
                } else {
                    log("Stopping module initialization");
                    return;
                }
            }

            Context = context;

            Context.Player.OnSessionChange += OnSessionChange;
            Context.Player.OnCharacterLogout += OnCharacterLogout;
            Context.Player.OnZoneChange += OnZoneChange;
            Context.FirstMonster.OnHPUpdate += OnHPUpdate;
            Context.FirstMonster.OnMonsterSpawn += OnMonsterSpawn;
            Context.FirstMonster.OnMonsterDespawn += OnMonsterDespawn;
            Context.FirstMonster.OnMonsterDeath += OnMonsterDeath;
            Context.SecondMonster.OnHPUpdate += OnHPUpdate;
            Context.SecondMonster.OnMonsterSpawn += OnMonsterSpawn;
            Context.SecondMonster.OnMonsterDespawn += OnMonsterDespawn;
            Context.SecondMonster.OnMonsterDeath += OnMonsterDeath;
            Context.ThirdMonster.OnHPUpdate += OnHPUpdate;
            Context.ThirdMonster.OnMonsterSpawn += OnMonsterSpawn;
            Context.ThirdMonster.OnMonsterDespawn += OnMonsterDespawn;
            Context.ThirdMonster.OnMonsterDeath += OnMonsterDeath;

            //no need for a real config file to check for one boolean
            showDetailedMessages = File.Exists("Modules\\" + Name + "\\DEBUG");
			
            try {
                errorLogWriter = new StreamWriter(File.Open("Modules\\" + Name + "\\errors.log", FileMode.Append, FileAccess.Write, FileShare.Read));
                errorLogWriter.AutoFlush = true;
            } catch (Exception e) {
                log("Error opening/creating error log: " + e.Message);
            }
			
            InitializeSessionAsync();
            syncThreadReference = new Thread(syncThread);

            //part of temporary workaround
            delay = UserSettings.PlayerConfig.Overlay.GameScanDelay;
        }

        public void Unload() {
            Context.Player.OnSessionChange -= OnSessionChange;
            Context.Player.OnCharacterLogout -= OnCharacterLogout;
            Context.Player.OnZoneChange -= OnZoneChange;
            Context.FirstMonster.OnHPUpdate -= OnHPUpdate;
            Context.FirstMonster.OnMonsterSpawn += OnMonsterSpawn;
            Context.FirstMonster.OnMonsterDespawn -= OnMonsterDespawn;
            Context.FirstMonster.OnMonsterDeath -= OnMonsterDeath;
            Context.SecondMonster.OnHPUpdate -= OnHPUpdate;
            Context.SecondMonster.OnMonsterSpawn += OnMonsterSpawn;
            Context.SecondMonster.OnMonsterDespawn -= OnMonsterDespawn;
            Context.SecondMonster.OnMonsterDeath -= OnMonsterDeath;
            Context.ThirdMonster.OnHPUpdate -= OnHPUpdate;
            Context.ThirdMonster.OnMonsterSpawn -= OnMonsterSpawn;
            Context.ThirdMonster.OnMonsterDespawn -= OnMonsterDespawn;
            Context.ThirdMonster.OnMonsterDeath -= OnMonsterDeath;

            for (int i = 0; i < Context.FirstMonster.Ailments.Count; i++) {
                Context.FirstMonster.Ailments[i].OnBuildupChange -= OnBuildupChange;
            }
            for (int i = 0; i < Context.SecondMonster.Ailments.Count; i++) {
                Context.SecondMonster.Ailments[i].OnBuildupChange -= OnBuildupChange;
            }
            for (int i = 0; i < Context.ThirdMonster.Ailments.Count; i++) {
                Context.ThirdMonster.Ailments[i].OnBuildupChange -= OnBuildupChange;
            }

            //part of temporary workaround
            UserSettings.PlayerConfig.Overlay.GameScanDelay = delay;

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
                log("Created session", true);
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
                error(e.GetType() + " occurred in get(" + url + "): " + e.Message);
                statusList.increment(Status.exception);
                Response response = new Response {
                    status = Status.exception,
                    value = "Exception in SyncPlugin.get"
                };
                return response;
            }
        }

        private void initializeAilments(Monster monster) {
            for (int i = 0; i < monster.Ailments.Count; i++) {
                monster.Ailments[i].OnBuildupChange += OnBuildupChange;
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
                if (showDetailedMessages) {
                    Debugger.Module(message, Name);
                }
            } else {
                Debugger.Module(message, Name);
            }
        }

        private void error(string message) {
			log(message);
            if (errorLogWriter != null) {
                errorLogWriter.WriteLine("[" + DateTime.UtcNow.ToString(new CultureInfo("en-gb")) + "] " + message);
            }
        }

        private void OnBuildupChange(object source, MonsterAilmentEventArgs args) {
            //ailment does not contain any information about the monster it is attached to, so every ailment on every monster has to be checked if it is the same object
            if (isPartyLeader) {
                if (processBuildup(Context.FirstMonster, (Ailment)source)) {
                    return;
                }
                if (processBuildup(Context.SecondMonster, (Ailment)source)) {
                    return;
                }
                processBuildup(Context.ThirdMonster, (Ailment)source);
            }
        }

        private void OnCharacterLogout(object source, EventArgs args) {
            if (isInParty) {
                quitSession();
                if (syncThreadReference.IsAlive) {
                    stopSyncThread();
                }
            }
        }

        private void OnHPUpdate(object source, EventArgs args) {
            if (isPartyLeader) {
                pushPartHP((Monster)source);
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

        private void OnMonsterSpawn(object source, MonsterSpawnEventArgs args) {
            initializeAilments((Monster)source);
        }

        private void OnSessionChange(object source, EventArgs args) {
            if (isInParty) { //quit old session
                quitSession();
                if (syncThreadReference.IsAlive) {
                    stopSyncThread();
                }
            }
            SessionID = Context.Player.SessionID;
            InitializeSessionAsync();
        }

        private void OnZoneChange(object source, EventArgs args) {
            if (isInParty) { //quit old session
                quitSession();
                if (syncThreadReference.IsAlive) {
                    stopSyncThread();
                }
            }
            InitializeSessionAsync();
        }

        private async void InitializeSessionAsync() {
            SessionID = Context.Player.SessionID;
            try {
                await System.Threading.Tasks.Task.Yield();
                Thread.Sleep(1000);
                if (Context.Player.InPeaceZone || Context.Player.ZoneID == 504 || string.IsNullOrEmpty(SessionID)) { //if player is in peace zone/training area or sessionID is empty
                    if (isInParty) {
                        quitSession();
                        if (syncThreadReference.IsAlive) {
                            stopSyncThread();
                        }
                    }
                    return;
                }

                for (int i = 0; i < Context.Player.PlayerParty.Members.Count; i++) { //check if player is party leader
                    if (Context.Player.PlayerParty.Members[i].IsPartyLeader) {
                        PartyLeader = Context.Player.PlayerParty.Members[i].Name;
                    }

                    if (Context.Player.PlayerParty.Members[i].IsMe && Context.Player.PlayerParty.Members[i].IsPartyLeader && Context.Player.PlayerParty.Members[i].IsInParty) { //if player is party leader
                        isPartyLeader = true;
                        isInParty = createPartyIfNotExist();
                    } else if (Context.Player.PlayerParty.Members[i].IsMe && !Context.Player.PlayerParty.Members[i].IsPartyLeader && Context.Player.PlayerParty.Members[i].IsInParty) { //if player is not party leader
                        isPartyLeader = false;
                        isInParty = partyExists();
                        if (isInParty) { //if party leader has SyncPlugin installed and enabled
                            log("Entered " + PartyLeader + "\'s session");
                            //stopMonsterThreads();
                            startSyncThread();
                        } else {
                            log("There is no session to enter", true);
                            //if (monsterThreadsStopped) {
                            //    startMonsterThreads();
                            //}
                        }
                    }
                }
            }
            catch (NullReferenceException) { //happened while closing the game, needs further testing
                log("NullReferenceException in InitializeSessionAsync()");
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

        private bool processBuildup(Monster monster, Ailment ailment) {
            int value;
            for (int i = 0; i < monster.Ailments.Count; i++) { //check if ailment is on target, if so send data to server
                if (monster.Ailments[i].Equals(ailment)) {
                    if (float.IsNaN(ailment.Buildup)) {
                        value = 0;
                    } else {
                        value = (int)ailment.Buildup;
                    }
                    pushAilment(monster.MonsterNumber - 1, i, value);
                    return true;
                }
            }
            return false; //ailment has not been found
        }

        private void pullAilmentBuildup(Monster monster) {
            Response result;
            for (int i = 0; i < monster.Ailments.Count; i++) {
                result = get(sessionUrlString + "/monster/" + (monster.MonsterNumber - 1) + "/ailment/" + i + "/buildup");
                if (result.status == Status.ok) {
                    monster.Ailments[i].Buildup = int.Parse(result.value);
                } else {
                    if (statusList.count(result.status) == 1) { //only show first error of each type; status count has already been incremented if error has occurred
                        error("Error in pullAilmentBuildup: " + result.value);
                    }
                }
            }
        }

        private void pullPartHP(Monster monster) {
            Response result;
            for (int i = 0; i < monster.Parts.Count; i++) {
                result = get(sessionUrlString + "/monster/" + (monster.MonsterNumber - 1) + "/part/" + i + "/hp");
                if (result.status == Status.ok) {
                    monster.Parts[i].Health = int.Parse(result.value);
                } else {
                    if (statusList.count(result.status) == 1) { //only show first error of each type; status count has already been incremented if error has occurred
                        error("Error in pullPartHP: " + result.value);
                    }
                }
            }
        }

        private void pushAilment(int monsterindex, int ailmentindex, int buildup) {
            Response result = get(sessionUrlString + "/monster/" + monsterindex + "/ailment/" + ailmentindex + "/buildup/" + buildup);
            if (result.status != Status.ok) {
                if (statusList.count(result.status) == 1) { //only show first error of each type; status count has already been incremented if error has occurred
                    error("Error in pushAilment: " + result.value);
                }
            }
        }

        private void pushPartHP(Monster monster) {
            float hp;
            Response result;
            for (int i = 0; i < monster.Parts.Count; i++) {
                hp = monster.Parts[i].Health;
                if (float.IsNaN(hp)) {
                    hp = 0;
                }
                result = get(sessionUrlString + "/monster/" + (monster.MonsterNumber - 1) + "/part/" + i + "/hp/" + (int)hp);
                if (result.status != Status.ok) {
                    if (statusList.count(result.status) == 1) { //only show first error of each type; status count has already been incremented if error has occurred
                        error("Error in pushPartHP: " + result.value);
                    }
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
            isPartyLeader = false;
        }

        //private void startMonsterThreads() {
        //    Context.FirstMonster.StartThreadingScan();
        //    Context.SecondMonster.StartThreadingScan();
        //    Context.ThirdMonster.StartThreadingScan();
        //    monsterThreadsStopped = false;
        //    log("Started monster threads", true);
        //}

        private void startSyncThread() {
            if (syncThreadReference.IsAlive) {
                error("Error starting sync thread: it is already active");
                return;
            }

            //part of temporary workaround
            delay = UserSettings.PlayerConfig.Overlay.GameScanDelay;
            UserSettings.PlayerConfig.Overlay.GameScanDelay = 2000;

            log("Started sync thread", true);
            syncThreadReference.Start();
        }

        //private void stopMonsterThreads() {
        //    Context.FirstMonster.StopThread();
        //    Context.SecondMonster.StopThread();
        //    Context.ThirdMonster.StopThread();
        //    monsterThreadsStopped = true;
        //    log("Stopped monster threads", true);
        //}

        private void stopSyncThread() {
            syncThreadReference.Abort();
            log("Stopped sync thread", true);

            //part of temporary workaround
            UserSettings.PlayerConfig.Overlay.GameScanDelay = delay;
        }

        private void syncThread() {
            while (Context.IsActive) {
                pullPartHP(Context.FirstMonster);
                pullPartHP(Context.SecondMonster);
                pullPartHP(Context.ThirdMonster);
                pullAilmentBuildup(Context.FirstMonster);
                pullAilmentBuildup(Context.SecondMonster);
                pullAilmentBuildup(Context.ThirdMonster);
                Thread.Sleep(delay);
            }
        }
    }
}