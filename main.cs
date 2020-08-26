﻿using System;
using System.Collections.Generic;
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

        public void increment(Status status) => list[status]++;
    }

    public class SyncPlugin : IPlugin {
        private const string ServerUrl = "http://mhwsync.herokuapp.com";
        private string partyLeader = "";
        private int retries = 5;
        private string sessionID = "";
        private string sessionUrlString = "";
        private Thread syncThreadReference;

        private readonly StatusList statusList = new StatusList();

        public Game Context { get; set; }
        public string Description { get; set; }
        public string Name { get; set; }
        private bool isInParty { get; set; } = false;
        private bool isPartyLeader { get; set; } = false;
        private bool monsterThreadsStopped { get; set; } = false;

        private string PartyLeader {
            get => partyLeader;
            set {
                partyLeader = HttpUtility.UrlEncode(value);
                sessionUrlString = ServerUrl + "/session/" + SessionID + PartyLeader;
            }
        }

        private string SessionID {
            get => sessionID;
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
        }

        private void clearMonster(int monsterIndex) {
            get(sessionUrlString + "/monster/" + monsterIndex + "/clear");
        }

        private bool createPartyIfNotExist() {
            if (partyExists()) {
                log("Did not create session because it already exists");
                return true;
            }

            Response r = get(sessionUrlString + "/create");
            if (r.status == Status.ok) {
                log("Created session");
                return true;
            } else {
                log("Error creating session: " + r.status + " - " + r.value);
            }
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
                log(e.GetType() + " occurred in get(" + url + "): " + e.Message);
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

        private void log(string message) {
            Debugger.Module(message, Name);
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
                stopSyncThread();
            }
            SessionID = Context.Player.SessionID;
            InitializeSessionAsync();
        }

        private void OnZoneChange(object source, EventArgs args) {
            InitializeSessionAsync();
        }

        private async void InitializeSessionAsync() {
            await System.Threading.Tasks.Task.Yield();
            Thread.Sleep(2000);
            if (Context.Player.InPeaceZone) {
                if (isInParty) {
                    quitSession();
                }
                return;
            }

            for (int i = 0; i < Context.Player.PlayerParty.Members.Count; i++) { //check if player is party leader
                if (Context.Player.PlayerParty.Members[i].IsPartyLeader) {
                    PartyLeader = Context.Player.PlayerParty.Members[i].Name;
                }

                if (Context.Player.PlayerParty.Members[i].IsMe && Context.Player.PlayerParty.Members[i].IsPartyLeader && Context.Player.PlayerParty.Members[i].IsInParty) { //player is party leader
                    isPartyLeader = true;
                    isInParty = createPartyIfNotExist();
                    if (!isInParty) {
                        log("Could not create session");
                    }
                } else if (Context.Player.PlayerParty.Members[i].IsMe && !Context.Player.PlayerParty.Members[i].IsPartyLeader && Context.Player.PlayerParty.Members[i].IsInParty) { //player is not party leader
                    isPartyLeader = false;
                    isInParty = partyExists();
                    if (isInParty) {
                        log("Entered session");
                        stopMonsterThreads();
                        startSyncThread();
                    } else {
                        log("There is no session to enter");
                        if (monsterThreadsStopped) {
                            startMonsterThreads();
                        }
                    }
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

        private bool processBuildup(Monster target, Ailment ailment) {
            int value;
            for (int i = 0; i < target.Ailments.Count; i++) { //check if ailment is on target, if so send data to server
                if (target.Ailments[i].Equals(ailment)) {
                    if (float.IsNaN(ailment.Buildup)) {
                        value = 0;
                    } else {
                        value = (int)ailment.Buildup;
                    }
                    pushAilment(target.MonsterNumber - 1, i, value);
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
                    monster.Parts[i].Health = int.Parse(result.value);
                } else {
                    if (statusList.count(result.status) == 1) { //only show first error of each type; status count has already been incremented if error has occurred
                        log("error in pullAilmentBuildup: " + result.value);
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
                        log("error in pullPartHP: " + result.value);
                    }
                }
            }
        }

        private void pushAilment(int monsterindex, int ailmentindex, int buildup) {
            Response result = get(sessionUrlString + "/monster/" + monsterindex + "/ailment/" + ailmentindex + "/buildup/" + buildup);
            if (result.status != Status.ok) {
                if (statusList.count(result.status) == 1) { //only show first error of each type; status count has already been incremented if error has occurred
                    log("error in pushAilment: " + result.value);
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
                        log("error in pushPartHP: " + result.value);
                    }
                }
            }
        }

        private void quitSession() {
            if (isPartyLeader) {
                get(sessionUrlString + "/delete");
            }
            if (statusList.errorCount > 0) {
                log(statusList.errorCount + " errors occurred in this session");
                if (statusList.errorCount > 0) {
                    log(statusList.ToString());
                }
                statusList.clear();
            }
            sessionID = "";
            partyLeader = "";
            isInParty = false;
            isPartyLeader = false;
            log("Left session");
        }

        private void startMonsterThreads() {
            Context.FirstMonster.StartThreadingScan();
            Context.SecondMonster.StartThreadingScan();
            Context.ThirdMonster.StartThreadingScan();
            monsterThreadsStopped = false;
            log("Started monster threads");
        }

        private void startSyncThread() {
            syncThreadReference = new Thread(syncThread);
            syncThreadReference.Start();
        }

        private void stopMonsterThreads() {
            Context.FirstMonster.StopThread();
            Context.SecondMonster.StopThread();
            Context.ThirdMonster.StopThread();
            monsterThreadsStopped = true;
            log("Stopped monster threads");
        }

        private void stopSyncThread() {
            syncThreadReference.Abort();
        }

        private void syncThread() {
            while (Context.IsActive) {
                pullPartHP(Context.FirstMonster);
                pullPartHP(Context.SecondMonster);
                pullPartHP(Context.ThirdMonster);
                pullAilmentBuildup(Context.FirstMonster);
                pullAilmentBuildup(Context.SecondMonster);
                pullAilmentBuildup(Context.ThirdMonster);
                Thread.Sleep(UserSettings.PlayerConfig.Overlay.GameScanDelay);
            }
        }
    }
}
