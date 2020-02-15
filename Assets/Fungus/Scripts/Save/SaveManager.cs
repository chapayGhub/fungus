﻿// This code is part of the Fungus library (https://github.com/snozbot/fungus)
// It is released for free under the MIT open source license (https://github.com/snozbot/fungus/blob/master/LICENSE)

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

//TODO update doco
//  should really enforce slots

namespace Fungus
{
    /// <summary>
    /// Manages the Save History (a list of Save Points) and provides a set of operations for saving and loading games.
    ///
    /// Note WebGL and Webplayer (deprecated) save using playerprefs instead of using a json file in persistent storage
    /// -webgl would require additional js to force a sync of FS.syncfs
    /// -webplayer does not implement system io
    /// </summary>
    public class SaveManager : MonoBehaviour
    {
        /// <summary>
        /// Meta data about a save that has found by a save manager.
        /// These exist to prevent the Save Manager from keeping potentially a lot of potentially
        /// very large json files in ram.
        /// </summary>
        public class SavePointMeta
        {
            public string saveName;
            public string savePointDescription;
            public System.DateTime savePointLastWritten;
            public string fileLocation;
            public string progressMarker;
        }

        [SerializeField] protected List<SavePointMeta> saveMetas = new List<SavePointMeta>();

        /// <summary>
        /// Access list of all currently known saves, for the current profile.
        /// </summary>
        public List<SavePointMeta> SaveMetas { get { return saveMetas; } }

        [SerializeField] protected string currentSaveProfileKey = string.Empty;

        /// <summary>
        /// Profiles determine which set of saves are available.
        /// </summary>
        public string CurrentSaveProfileKey { get { return currentSaveProfileKey; } }

#if UNITY_WEBPLAYER || UNITY_WEBGL
        [System.Serializable]
        public class WebSaveBlob
        {
            public List<string> saveJSONs = new List<string>();
        }

        [SerializeField] protected WebSaveBlob webSaveBlob = new WebSaveBlob();
#endif

        /// <summary>
        /// POD for info the SaveManager wants between runs of the game.
        /// </summary>
        [System.Serializable]
        protected class SaveManagerData
        {
            public string lastProfileName;
        }

        public static string STORAGE_DIRECTORY { get { return Application.persistentDataPath + "/FungusSaves/"; } }
        protected const string FileExtension = ".save";

        /// <summary>
        /// Directory location currently being used for saves.
        /// </summary>
        /// <returns></returns>
        private string GetFullSaveDir()
        {
            return System.IO.Path.GetFullPath(STORAGE_DIRECTORY + currentSaveProfileKey + "/");
        }

        /// <summary>
        /// Filename being used for the save manager persisted data.
        /// </summary>
        /// <returns></returns>
        private string GetSaveManagerDataFile()
        {
            return System.IO.Path.GetFullPath(STORAGE_DIRECTORY + "save_manager_data.json");
        }

        /// <summary>
        /// The scene that should be loaded when restarting a game.
        /// </summary>
        public string StartScene { get; set; }

        /// <summary>
        /// Returns the number of Save Points in the Save History.
        /// </summary>
        public virtual int NumSaves { get { return saveMetas.Count; } }

        protected int numAutoSaves = 1, numUserSaves = 0;

        /// <summary>
        /// SaveManager wants to know how many UserSaves are expected, this is set via the ConfigureSaveNumber.
        /// </summary>
        public int NumberOfUserSaves { get { return numUserSaves; } }

        /// <summary>
        /// SaveManager wants to know how many Auto Saves are allowed, this is set via the ConfigureSaveNumber.
        /// </summary>
        public int NumberOfAutoSaves { get { return numAutoSaves; } }

        /// <summary>
        /// Determines the number of saves expected and maintained for the current profile by the save manager.
        ///
        /// PopulatesSaveMetas when called.
        /// </summary>
        /// <param name="numAutos">Max auto saves, after which, the oldest will be removed</param>
        /// <param name="numUser">Slots for user saves that will be maintained</param>
        public void ConfigureSaveNumber(int numAutos, int numUser)
        {
            numAutoSaves = numAutos;
            numUserSaves = numUser;
            PopulateSaveMetas();
        }

        /// <summary>
        /// Set during SaveManager loading, intended to be used by any class that wants conditional logic
        /// for a 'normal' level load vs one caused by a the save manager.
        /// </summary>
        public bool IsSaveLoading { get; protected set; }

        public void Awake()
        {
            IsSaveLoading = false;
            StartScene = SceneManager.GetActiveScene().name;

            //load last used profile
            try
            {
                var fileName = GetSaveManagerDataFile();
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fileName));
                var datString = System.IO.File.ReadAllText(fileName);
                var dat = JsonUtility.FromJson<SaveManagerData>(datString);
                if (dat != null)
                {
                    ChangeProfile(dat.lastProfileName);
                }
            }
            catch (Exception)
            {
                //if that fails for whatever reason use default profile
                ChangeProfile(FungusConstants.DefaultSaveProfileKey);
            }

            //we find that other systems want to take actions in start or enable or update, so lets not make that
            //  difficult to do.
            StartCoroutine(DelayGameStart());

            SceneManager.sceneLoaded += SceneManager_sceneLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= SceneManager_sceneLoaded;
        }

        private void SceneManager_sceneLoaded(Scene arg0, LoadSceneMode arg1)
        {
            GameStarted();
        }

        //we want to kick off this frame but we want to ensure all other starts get to go first
        protected IEnumerator DelayGameStart()
        {
            yield return new WaitForFixedUpdate();
            GameStarted();
        }

        /// <summary>
        /// Called on game start or when level is loaded. Will do nothing if level load is caued by the SaveManager
        /// </summary>
        private void GameStarted()
        {
            if (!IsSaveLoading)
            {
                //scene was loaded not a save game
                var savePoints = UnityEngine.Object.FindObjectsOfType<ProgressMarker>().ToList();
                var startingSavePoint = savePoints.FirstOrDefault(x => x.IsStartPoint);
                if (startingSavePoint != null)
                {
                    startingSavePoint.GetFlowchart().ExecuteBlock(startingSavePoint.ParentBlock, startingSavePoint.CommandIndex);
                }
            }
        }

        /// <summary>
        /// Profiles determine which set of saves are available to the user.
        /// </summary>
        /// <param name="saveProfileKey"></param>
        public void ChangeProfile(string saveProfileKey)
        {
            if (saveProfileKey != currentSaveProfileKey)
            {
                currentSaveProfileKey = saveProfileKey;

                var fileName = GetSaveManagerDataFile();
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fileName));
                var profile = new SaveManagerData() { lastProfileName = currentSaveProfileKey };
                System.IO.File.WriteAllText(fileName, JsonUtility.ToJson(profile));

                PopulateSaveMetas();
                SaveManagerSignals.DoSaveProfileChanged();
                SaveManagerSignals.DoSaveReset();
            }
        }

        //TODO needs web version
        /// <summary>
        /// Gathers all saves for the current profile, filling the SaveMetas collection.
        ///
        /// If there are less existing user saves that configured, empty metas are generated.
        /// </summary>
        public void PopulateSaveMetas()
        {
            saveMetas.Clear();

            var dir = GetFullSaveDir();

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dir));

            var foundFiles = System.IO.Directory.GetFiles(dir, "*" + FileExtension);

            foreach (var item in foundFiles)
            {
                var fileContents = System.IO.File.ReadAllText(item);
                var save = SavePointData.DecodeFromJSON(fileContents);
                GenerateMetaFromSave(item, save);
            }

            //TODO look at the settings and ensure we have saves in correct order for user saves and put dumbies in where we don't
            var userSaves = CollectUserSaves();


            for (int i = 0; i < NumberOfUserSaves; i++)
            {
                if (userSaves.Find(x => x.saveName.EndsWith(i.ToString())) == null)
                {
                    saveMetas.Add(new SavePointMeta() { saveName = FungusConstants.UserSavePrefix + i.ToString() });
                }
            }
        }

        /// <summary>
        /// Helpder to create the meta from a fullsave
        /// </summary>
        /// <param name="fileLoc"></param>
        /// <param name="save"></param>
        private void GenerateMetaFromSave(string fileLoc, SavePointData save)
        {
            if (save != null)
            {
                saveMetas.Add(new SavePointMeta()
                {
                    fileLocation = fileLoc,
                    saveName = save.SaveName,
                    progressMarker = save.ProgressMarkerName,
                    savePointDescription = save.SavePointDescription,
                    savePointLastWritten = save.LastWritten,
                });
            }
        }

        public int SaveNameToIndex(string saveName)
        {
            return saveMetas.FindIndex(x => x.saveName == saveName);
        }

        /// <summary>
        /// Deletes a previously stored Save History from persistent storage.
        /// </summary>
        public void DeleteSave(int index, bool suppressReplaceSlot = false)
        {
            var meta = saveMetas[index];
#if UNITY_WEBPLAYER || UNITY_WEBGL
            webSaveBlob.saveJSONs.RemoveAt(index);
            var webBlogJSON = JsonUtility.ToJson(webSaveBlob);
            PlayerPrefs.SetString(currentSaveDataKey, webBlogJSON);
            PlayerPrefs.Save();
#else
            if (System.IO.File.Exists(meta.fileLocation))
            {
                System.IO.File.Delete(meta.fileLocation);
            }
#endif//UNITY_WEBPLAYER
            if (meta.saveName.StartsWith(FungusConstants.UserSavePrefix) && !suppressReplaceSlot)
            {
                saveMetas.Add(new SavePointMeta() { saveName = meta.saveName });
            }
            saveMetas.RemoveAt(index);
            SaveManagerSignals.DoSaveDeleted(meta.saveName);
        }

        public void DeleteSave(SavePointMeta meta)
        {
            DeleteSave(saveMetas.IndexOf(meta));
        }

        /// <summary>
        /// Creates a new Save Point using a key and description.
        /// </summary>
        public virtual void Save(string saveName, string savePointDescription, bool isAutoSave = false)
        {
            SaveManagerSignals.DoSavePrepare(saveName, savePointDescription);

            var existingMetaIndex = SaveNameToIndex(saveName);
            if (existingMetaIndex >= 0)
            {
                DeleteSave(existingMetaIndex, true);
            }

            var savePointDataJSON = SavePointData.EncodeToJson(saveName, savePointDescription, out SavePointData save);
            var fileName = GetFullSaveDir() + (isAutoSave ? FungusConstants.AutoSavePrefix : FungusConstants.UserSavePrefix)
                + System.DateTime.Now.ToString("yyyy-MM-dd-hh-mm-ss.ffff") + FileExtension;
            GenerateMetaFromSave(fileName, save);
#if UNITY_WEBPLAYER || UNITY_WEBGL

#else
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fileName));
            System.IO.File.WriteAllText(fileName, savePointDataJSON, System.Text.Encoding.UTF8);
#endif

            //if we limit autos and it is an auto, are there now to many, delete oldest until not over limit
            if (isAutoSave && NumberOfAutoSaves >= 0)
            {
                var autoSaves = CollectAutoSaves();

                for (int i = 0; i < autoSaves.Count - NumberOfAutoSaves; i++)
                {
                    DeleteSave(saveMetas.IndexOf(autoSaves[i]), true);
                }
            }

            SaveManagerSignals.DoSaveSaved(saveName, savePointDescription);
        }

        /// <summary>
        /// Helper to call LoadSavePoint via a meta.
        /// </summary>
        /// <param name="meta"></param>
        /// <returns></returns>
        public virtual bool Load(SavePointMeta meta)
        {
            var saveContent = System.IO.File.ReadAllText(meta.fileLocation, System.Text.Encoding.UTF8);

            var savePointData = SavePointData.DecodeFromJSON(saveContent);

            if (!LoadSavePoint(savePointData))
            {
                Debug.LogError("Failed to Load " + meta.saveName);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Cause a scene load, flagging that we are loading a save during it, in IsSaveLoading.
        /// When scene is loaded, we ask the savepoint to RunDeserialize. Means the serializers
        /// need to exist in target scene, either existing in all scenes manually, or singlying
        /// via a Don'tDestroyOnLoad
        /// </summary>
        /// <param name="savePointData"></param>
        /// <returns></returns>
        public virtual bool LoadSavePoint(SavePointData savePointData)
        {
            if (savePointData == null)
                return false;

            var markerKey = savePointData.ProgressMarkerName;

            UnityEngine.Events.UnityAction<Scene, LoadSceneMode> onSceneLoadedAction = null;

            onSceneLoadedAction = (scene, mode) =>
            {
                // Additive scene loads and non-matching scene loads could happen if the client is using the
                // SceneManager directly. We just ignore these events and hope they know what they're doing!
                if (mode == LoadSceneMode.Additive ||
                    scene.name != savePointData.SceneName)
                {
                    return;
                }

                SceneManager.sceneLoaded -= onSceneLoadedAction;

                // Look for a SaveData component in the scene to process the save data items.
                savePointData.RunDeserialize();

                SaveManagerSignals.DoSaveLoaded(savePointData.SaveName);

                // Execute Save Point Loaded event handlers with matching key.
                SaveLoaded.NotifyEventHandlers(savePointData.ProgressMarkerName);

                StartCoroutine(DelaySetNotLoading());
            };

            SceneManager.sceneLoaded += onSceneLoadedAction;
            IsSaveLoading = true;
            SaveManagerSignals.DoSavePreLoad(savePointData.SaveName);
            SceneManager.LoadScene(savePointData.SceneName);

            return true;
        }

        /// <summary>
        /// Used to allow full enable, start, update to run before turning off our loading flag.
        /// </summary>
        /// <returns></returns>
        private System.Collections.IEnumerator DelaySetNotLoading()
        {
            yield return new WaitForEndOfFrame();
            IsSaveLoading = false;
        }

        /// <summary>
        /// Deletes all Save Points in the Save History.
        /// </summary>
        public virtual void DeleteAllSaves()
        {
            for (int i = saveMetas.Count - 1; i >= 0; i--)
            {
                DeleteSave(i);
            }
        }

        /// <summary>
        /// Return the most recently written save regardless of type of save.
        /// </summary>
        /// <returns></returns>
        public virtual SavePointMeta GetMostRecentSave()
        {
            if (SaveMetas.Count > 0)
            {
                var newestSaveTime = SaveMetas.Max(x => x.savePointLastWritten);

                return SaveMetas.FirstOrDefault(x => x.savePointLastWritten == newestSaveTime);
            }

            return null;
        }

        /// <summary>
        /// Gather and return all Auto saves currently in our meta list.
        /// </summary>
        /// <returns></returns>
        public List<SavePointMeta> CollectAutoSaves()
        {
            return FungusManager.Instance.SaveManager.SaveMetas.Where(x => x.saveName.StartsWith(FungusConstants.AutoSavePrefix))
                .OrderBy(x => x.savePointLastWritten.Ticks).ToList();
        }

        /// <summary>
        /// Gather and return all User (slot) saves currently in our meta list.
        /// </summary>
        /// <returns></returns>
        public List<SavePointMeta> CollectUserSaves()
        {
            return FungusManager.Instance.SaveManager.SaveMetas.Where(x => x.saveName.StartsWith(FungusConstants.UserSavePrefix))
                .OrderBy(x => System.Convert.ToInt32(x.saveName.Substring(FungusConstants.UserSavePrefix.Length))).ToList();
        }

        /// <summary>
        /// Reload the starting scene, without setting the loading flag. If requested, can delete all saves on
        /// the current profile.
        /// </summary>
        /// <param name="deleteSaves"></param>
        /// <returns></returns>
        public virtual bool Restart(bool deleteSaves)
        {
            if (string.IsNullOrEmpty(StartScene))
            {
                Debug.LogError("No start scene specified");
                return false;
            }

            // Reset the Save History for a new game
            if (deleteSaves)
            {
                DeleteAllSaves();
            }

            SaveManagerSignals.DoSaveReset();
            SceneManager.LoadScene(StartScene);
            return true;
        }
    }
}