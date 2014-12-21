﻿using KarmaTestAdapter.Commands;
using KarmaTestAdapter.Config;
using KarmaTestAdapter.Helpers;
using KarmaTestAdapter.KarmaTestResults;
using KarmaTestAdapter.Logging;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestWindow.Extensibility;
using Microsoft.VisualStudio.TestWindow.Extensibility.Model;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KarmaTestAdapter
{
    public class KarmaTestContainer : KarmaTestContainerBase
    {
        private KarmaTestContainerList _containerList;
        private Dictionary<string, string> _files = new Dictionary<string, string>();
        private IEnumerable<KarmaFileWatcher> _fileWatchers;

        public KarmaTestContainer(KarmaTestContainerList containerList, string source, IKarmaLogger logger)
            : this(containerList, source, logger, null, null)
        { }

        private KarmaTestContainer(KarmaTestContainerList containerList, string source, IKarmaLogger logger, KarmaConfig config, Dictionary<string, string> files, DateTime? timeStamp = null)
            : base(containerList.Discoverer, source, timeStamp ?? DateTime.Now)
        {
            logger.Info("KarmaTestContainer.Create");
            this.Logger = logger;
            this.Settings = new KarmaSettings(Source, Logger);
            this._containerList = containerList;
            this.Config = config ?? KarmaGetConfigCommand.GetConfig(Source, Logger);
            this._files = files;
            if (this._files == null || this._files.Count == 0)
            {
                this._files = GetFiles();
            }
            SetCurrentHash(Settings.SettingsFile);
            SetCurrentHash(Settings.KarmaConfigFile);
            _fileWatchers = GetFileWatchers().Where(w => w != null).ToList();
        }

        private KarmaTestContainer(KarmaTestContainer copy, DateTime timeStamp)
            : this(copy._containerList, copy.Source, copy.Logger, copy.Config, copy._files)
        {
        }

        public IKarmaLogger Logger { get; private set; }
        public KarmaSettings Settings { get; private set; }
        public Uri ExecutorUri { get { return Globals.ExecutorUri; } }
        public Karma Karma { get; set; }
        public KarmaConfig Config { get; private set; }
        public string BaseDirectory { get { return KarmaTestContainerDiscoverer.BaseDirectory; } }

        private Dictionary<string, string> GetFiles()
        {
            return Config.GetFiles().ToDictionary(f => f, f => Sha1Utils.GetHash(f, null), StringComparer.OrdinalIgnoreCase);
        }

        private IEnumerable<KarmaFileWatcher> GetFileWatchers()
        {
            yield return CreateFileWatcher(Settings.SettingsFile);
            yield return CreateFileWatcher(Settings.KarmaConfigFile);
            foreach (var filter in Config.Files.GroupBy(f => f.FileFilter, StringComparer.OrdinalIgnoreCase))
            {
                var dirs = filter.Select(f => f.Directory);
                foreach (var dir in dirs.Where(d1 => !dirs.Any(d2 => !string.Equals(d1, d2, StringComparison.OrdinalIgnoreCase) && d1.StartsWith(d2, StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    yield return CreateFileWatcher(dir, filter.Key, true);
                }
            }
        }

        private KarmaFileWatcher CreateFileWatcher(string file)
        {
            if (!string.IsNullOrWhiteSpace(file))
            {
                return CreateFileWatcher(Path.GetDirectoryName(file), Path.GetFileName(file), false);
            }
            return null;
        }

        private KarmaFileWatcher CreateFileWatcher(string directory, string filter, bool includeSubdirectories)
        {
            var watcher = new KarmaFileWatcher(directory, filter, includeSubdirectories);
            watcher.Changed += FileWatcherChanged;
            Logger.Info(@"Watching '{0}'", PathUtils.GetRelativePath(BaseDirectory, watcher.Watching, true));
            return watcher;
        }

        private void FileWatcherChanged(object sender, TestFileChangedEventArgs e)
        {
            switch (e.ChangedReason)
            {
                case TestFileChangedReason.Added:
                    FileAdded(e.File);
                    break;
                case TestFileChangedReason.Changed:
                case TestFileChangedReason.Saved:
                    FileChanged(e.File);
                    break;
                case TestFileChangedReason.Removed:
                    FileRemoved(e.File);
                    break;
            }
        }

        private object _fileChangeLock = new object();
        private bool SetCurrentHash(string file)
        {
            lock (_fileChangeLock)
            {
                if (!string.IsNullOrWhiteSpace(file))
                {
                    var currentHash = GetCurrentHash(file);
                    if (System.IO.File.Exists(file))
                    {
                        var newHash = Sha1Utils.GetHash(file, currentHash);
                        if (newHash != currentHash)
                        {
                            _files[file] = newHash;
                            return true;
                        }
                    }
                    else
                    {
                        _files.Remove(file);
                        return currentHash != null;
                    }
                }
                return false;
            }
        }

        private string GetCurrentHash(string file)
        {
            lock (_fileChangeLock)
            {
                string hash;
                if (_files.TryGetValue(file, out hash))
                {
                    return hash;
                }
                return null;
            }
        }

        public bool FileAdded(string file)
        {
            return FileChanged(file, string.Format("File added:   {0}", file), f => SetCurrentHash(f) || true);
        }

        public bool FileChanged(string file)
        {
            return FileChanged(file, string.Format("File changed: {0}", file), f => SetCurrentHash(f));
        }

        public bool FileRemoved(string file)
        {
            return FileChanged(file, string.Format("File removed: {0}", file), f => _files.Remove(f) || true);
        }

        public bool FileChanged(string file, string reason, Func<string, bool> hasChanged)
        {
            lock (_fileChangeLock)
            {
                if (_files.ContainsKey(file) || Config.HasFile(file) || PathUtils.PathsEqual(file, Settings.KarmaConfigFile) || PathUtils.PathsEqual(file, Settings.SettingsFile))
                {
                    // The file belongs to this container
                    if (hasChanged(file))
                    {
                        TimeStamp = DateTime.Now;
                        if (PathUtils.PathsEqual(file, Settings.KarmaConfigFile) || PathUtils.PathsEqual(file, Settings.SettingsFile))
                        {
                            if (System.IO.File.Exists(Settings.Source))
                            {
                                KarmaTestContainerDiscoverer.AddTestContainerIfTestFile(Settings.Source);
                            }
                            else
                            {
                                KarmaTestContainerDiscoverer.RemoveTestContainer(Settings.Source);
                            }
                        }
                        else
                        {
                            KarmaTestContainerDiscoverer.RefreshTestContainers(reason);
                        }
                        return true;
                    }
                }
                return false;
            }
        }

        //public KarmaTestContainer Refresh()
        //{
        //    if (ShouldRefresh)
        //    {
        //        TimeStamp = DateTime.Now;
        //    }
        //    return this;
        //}

        public override string ToString()
        {
            return this.ExecutorUri.ToString() + "/" + this.Source;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_fileWatchers != null)
                {
                    foreach (var watcher in _fileWatchers)
                    {
                        Logger.Info(@"Stop watching '{0}'", PathUtils.GetRelativePath(BaseDirectory, watcher.Watching, true));
                        watcher.Dispose();
                    }
                    _fileWatchers = null;
                }
                if (Settings != null)
                {
                    Settings.Dispose();
                    Settings = null;
                }
            }
        }
    }
}
