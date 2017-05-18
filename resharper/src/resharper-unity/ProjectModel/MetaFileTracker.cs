﻿using System;
using JetBrains.Application.changes;
using JetBrains.DataFlow;
using JetBrains.ProjectModel;
using JetBrains.Util;

namespace JetBrains.ReSharper.Plugins.Unity.ProjectModel
{
    [SolutionComponent]
    public class MetaFileTracker : IChangeProvider
    {
        private static readonly DateTime UnixTime = new DateTime(1970, 1, 1, 0, 0, 0);

        private readonly ISolution mySolution;
        private readonly ILogger myLogger;
        private IProjectItem myLastAddedItem;

        public MetaFileTracker(Lifetime lifetime, ChangeManager changeManager, ISolution solution, ILogger logger)
        {
            mySolution = solution;
            myLogger = logger;
            changeManager.RegisterChangeProvider(lifetime, this);
            changeManager.AddDependency(lifetime, this, solution);
        }

        public object Execute(IChangeMap changeMap)
        {
            var projectModelChange = changeMap.GetChange<ProjectModelChange>(mySolution);
            if (projectModelChange == null) return null;
            if (projectModelChange.IsOpeningSolution || projectModelChange.IsClosingSolution)
                return null;

            if (projectModelChange.GetNewProject().IsUnityProject()
                || projectModelChange.GetOldProject().IsUnityProject())
            {
                return null;
            }

            myLogger.Verbose("resharper-unity: ProjectModelChange start");
            myLogger.Verbose("resharper-unity: " + projectModelChange.Dump());
            projectModelChange.Accept(new Visitor(this, myLogger));
            myLogger.Verbose("resharper-unity: ProjectModelChange end");
            return null;
        }

        private class Visitor : RecursiveProjectModelChangeDeltaVisitor
        {
            private readonly MetaFileTracker myMetaFileTracker;
            private readonly ILogger myLogger;

            public Visitor(MetaFileTracker metaFileTracker, ILogger logger)
            {
                myMetaFileTracker = metaFileTracker;
                myLogger = logger;
            }

            public override void VisitItemDelta(ProjectItemChange change)
            {
                // Ugh. There is an assert in ToString for ParentFolder != null, and the project folder has yes, ParentFolder == null
                if (change.ProjectItem.ParentFolder == null)
                    myLogger.Verbose("resharper-unity: Project item {0} {1}", change.ProjectItem.GetType().Name, change.ProjectItem.Name);
                else
                    myLogger.Verbose("resharper-unity: Project item {0}", change.ProjectItem);

                // When a project is reloaded, we get a removal notification for it and all of its
                // files, followed by a load of addition notifications. If we don't handle this
                // properly, we'll delete a load of .meta files and create new ones, causing big problems.
                // So ignore any project removal messages. We can safely ignore them, as a) Unity will
                // never do this, and b) you can't delete a project from Visual Studio/Rider, only remove it
                if (change.ProjectItem is IProject && change.IsRemoved)
                    return;

                var shouldRecurse = true;
                if (IsRenamedAsset(change))
                    shouldRecurse = OnItemRenamed(change);
                else if (IsAddedAsset(change))
                    OnItemAdded(change);
                else if (IsRemovedAsset(change))
                    OnItemRemoved(change);

                if (shouldRecurse)
                    base.VisitItemDelta(change);
            }

            private static bool IsRenamedAsset(ProjectItemChange change)
            {
                return change.IsMovedOut && ShouldHandleChange(change);
            }

            private bool IsAddedAsset(ProjectItemChange change)
            {
                if (change.IsAdded && ShouldHandleChange(change))
                    return true;
                return change.IsContentsExternallyChanged && myMetaFileTracker.myLastAddedItem != null &&
                       myMetaFileTracker.myLastAddedItem.Location == change.ProjectItem.Location &&
                       ShouldHandleChange(change);
            }

            private static bool IsRemovedAsset(ProjectItemChange change)
            {
                return change.IsRemoved && ShouldHandleChange(change);
            }

            private static bool ShouldHandleChange(ProjectItemChange change)
            {
                return !(change.ProjectItem is IProject) && change.GetOldProject().IsUnityProject() && IsAsset(change) && !IsItemMetaFile(change);
            }

            private static bool IsAsset(ProjectItemChange change)
            {
                var rootFolder = GetRootFolder(change.OldParentFolder);
                return rootFolder != null && string.Compare(rootFolder.Name, "Assets", StringComparison.OrdinalIgnoreCase) == 0;
            }

            private static IProjectFolder GetRootFolder(IProjectItem item)
            {
                while (item?.ParentFolder != null && item.ParentFolder.Kind != ProjectItemKind.PROJECT)
                {
                    item = item.ParentFolder;
                }
                return item as IProjectFolder;
            }

            private static bool IsItemMetaFile(ProjectItemChange change)
            {
                return change.ProjectItem.Location.ExtensionWithDot.Equals(".meta", StringComparison.OrdinalIgnoreCase);
            }

            private bool OnItemRenamed(ProjectItemChange change)
            {
                myLogger.Verbose("*** resharper-unity: Item renamed {0} -> {1}", change.OldLocation, change.ProjectItem.Location);

                var oldMetaFile = change.OldParentFolder.Location.Combine(change.OldLocation.Name + ".meta");
                var newMetaFile = GetMetaFile(change.ProjectItem.Location);
                if (oldMetaFile.ExistsFile)
                    RenameMetaFile(oldMetaFile, newMetaFile, string.Empty);
                else
                    CreateMetaFile(newMetaFile);

                // Don't recurse for folder renames - the child contents will be "renamed", but
                // the old location will no longer be there, and the meta files don't need moving
                return !(change.ProjectItem is IProjectFolder);
            }

            private void OnItemAdded(ProjectItemChange change)
            {
                myLogger.Verbose("*** resharper-unity: Item added {0}", change.ProjectItem.Location);

                CreateMetaFile(GetMetaFile(change.ProjectItem.Location));

                // We sometimes get notified of an item being added when it's not actually on disk.
                // This appears to be happen:
                // 1) When loading a project, with a stale cache (it gets removed once loaded)
                // 2) When invoking the Move To Folder refactoring on a type. ReSharper adds a new file
                //    and then removes the existing file, but only if it's empty. We create a new meta
                //    file, and overwrite it with the existing one if the empty file is removed.
                myMetaFileTracker.myLastAddedItem = null;
                if (change.ProjectItem.Location.Exists == FileSystemPath.Existence.Missing)
                    myMetaFileTracker.myLastAddedItem = change.ProjectItem;
            }

            private void OnItemRemoved(ProjectItemChange change)
            {
                myLogger.Verbose("*** resharper-unity: Item removed {0}", change.OldLocation);

                var metaFile = GetMetaFile(change.OldLocation);
                if (!metaFile.ExistsFile) return;

                if (IsMoveToFolderRefactoring(change))
                {
                    var newMetaFile = GetMetaFile(myMetaFileTracker.myLastAddedItem.Location);
                    RenameMetaFile(metaFile, newMetaFile, " via add/remove");
                }
                else
                    DeleteMetaFile(metaFile);

                myMetaFileTracker.myLastAddedItem = null;
            }

            private bool IsMoveToFolderRefactoring(ProjectItemChange change)
            {
                // Adding to one folder and removing from another is the Move To Folder refactoring. If
                // we get a remove event, that means the original file is empty, and we should treat it
                // like a rename
                return myMetaFileTracker.myLastAddedItem != null &&
                       myMetaFileTracker.myLastAddedItem.Name == change.OldLocation.Name &&
                       myMetaFileTracker.myLastAddedItem.Location != change.OldLocation;
            }

            private static FileSystemPath GetMetaFile(FileSystemPath location)
            {
                return FileSystemPath.Parse(location + ".meta");
            }

            private void CreateMetaFile(FileSystemPath path)
            {
                if (path.ExistsFile)
                    return;

                try
                {
                    var guid = Guid.NewGuid();
                    var timestamp = (long)(DateTime.UtcNow - UnixTime).TotalSeconds;
                    path.WriteAllText($"fileFormatVersion: 2\r\nguid: {guid:N}\r\ntimeCreated: {timestamp}");
                    myLogger.Info("*** resharper-unity: Meta added {0}", path);
                }
                catch (Exception e)
                {
                    myLogger.LogException(LoggingLevel.ERROR, e, ExceptionOrigin.Assertion,
                        $"Failed to create Unity meta file {path}");
                }
            }

            private void RenameMetaFile(FileSystemPath oldPath, FileSystemPath newPath, string extraDetails)
            {
                try
                {
                    myLogger.Info("*** resharper-unity: Meta renamed{2} {0} -> {1}", oldPath, newPath, extraDetails);
                    oldPath.MoveFile(newPath, true);
                }
                catch (Exception e)
                {
                    myLogger.LogException(LoggingLevel.ERROR, e, ExceptionOrigin.Assertion,
                        $"Failed to rename Unity meta file {oldPath} -> {newPath}");
                }
            }

            private void DeleteMetaFile(FileSystemPath path)
            {
                try
                {
                    if (path.ExistsFile)
                    {
#if DEBUG
                        myLogger.Info("*** resharper-unity: Meta removed (ish) {0}", path);
                        path.MoveFile(FileSystemPath.Parse(path + ".deleted"), true);
#else
                        myLogger.Info("*** resharper-unity: Meta removed {0}", path);
                        path.DeleteFile();
#endif
                    }

                }
                catch (Exception e)
                {
                    myLogger.LogException(LoggingLevel.ERROR, e, ExceptionOrigin.Assertion,
                        $"Failed to delete Unity meta file {path}");
                }
            }
        }
    }
}