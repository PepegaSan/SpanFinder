using Span.Models;
using Span.ViewModels;
using System.Collections.Generic;

namespace Span.Services
{
    /// <summary>
    /// 컨텍스트 메뉴 호스트 인터페이스. MainWindow가 구현하며,
    /// ContextMenuService가 메뉴 항목 실행 시 이 인터페이스를 통해 
    /// 파일 조작, 뷰 전환, 정렬 등의 액션을 MainWindow에 위임한다.
    /// </summary>
    public interface IContextMenuHost
    {
        bool HasClipboardContent { get; }
        /// <summary>All paths selected in the same folder as the right-clicked item (multi-select aware).</summary>
        IReadOnlyList<string> GetSelectedPathsForContextMenu(string clickedPath);
        void PerformCut(string path);
        void PerformCopy(string path);
        void PerformPaste(string targetFolderPath);
        void PerformDelete(string path, string itemName);
        void PerformRename(FileSystemViewModel item);
        /// <summary>Resolve path to the live ViewModel in a visible pane, then inline-rename.</summary>
        void PerformRenameByPath(string path);
        void PerformOpen(FileSystemViewModel item);
        void PerformOpenDrive(DriveItem drive);
        void PerformOpenFavorite(FavoriteItem fav);
        void PerformNewFolder(string parentFolderPath);
        void PerformNewFile(string parentFolderPath, string fileName);
        void PerformNewFileFromShellNew(string parentFolderPath, ShellNewItem shellNewItem);
        void PerformCompress(string[] paths);
        void PerformExtractHere(string zipPath);
        void PerformExtractTo(string zipPath);
        void AddToFavorites(string path);
        void RemoveFromFavorites(string path);
        bool IsFavorite(string path);
        void SetColorTag(string path, ItemColorTag tag);
        void SetColorTag(IReadOnlyList<string> paths, ItemColorTag tag);
        void EditFolderNote(string path);
        void RemoveRemoteConnection(string connectionId);
        void EditRemoteConnection(string connectionId);
        void PerformEjectDrive(DriveItem drive);
        void PerformDisconnectDrive(DriveItem drive);
        void SwitchViewMode(ViewMode mode);
        void ApplySort(string field);
        void ApplySortDirection(bool ascending);
        void ApplyGroupBy(string groupBy);
        string CurrentGroupBy { get; }
        void PerformSelectAll();
        void PerformSelectNone();
        void PerformInvertSelection();
        void PerformOpenInNewTab(string folderPath);
        void PerformOpenTerminal(string folderPath);
        void PerformRefresh();

        /// <summary>Issue #58: 폴더에 컬러 태그 지정/해제.</summary>
        void PerformSetFolderTag(FolderViewModel folder, Models.FolderTagColor color);
        void PerformUndo();
        void PerformShowProperties(string path);

        void CreateFavoriteGroup();
        void RenameFavoriteGroup(string groupId);
        void DeleteFavoriteGroup(string groupId);
        void MoveFavoriteToGroup(string path, string? groupId);
        IReadOnlyList<FavoriteGroupViewModel> GetFavoriteGroups();
    }
}
