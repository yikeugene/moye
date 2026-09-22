using Moye.Models;
using System.Windows.Media.Imaging;

namespace Moye.Services;

public interface INotebookRepository : IDisposable
{
    Task InitializeAsync();
    Task<IReadOnlyList<NotebookSummary>> ListAsync();
    Task<NotebookDocument?> LoadAsync(string id);
    Task SaveAsync(NotebookDocument document);
    Task DeleteAsync(string id);
    Task<AssetData> PutAssetAsync(string fileName, string contentType, byte[] bytes);
    Task<AssetData> GetAssetAsync(string id);
}

public interface IBackupService
{
    Task ExportAsync(string path, IReadOnlyList<NotebookDocument> documents, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotebookDocument>> ImportAsync(string path, CancellationToken cancellationToken = default);
}

public interface IPdfService
{
    Task<IReadOnlyList<NotePage>> ImportAsync(string path, CancellationToken cancellationToken = default);
    Task<BitmapSource> RenderAsync(NotePage page, double scale, CancellationToken cancellationToken = default);
    Task ExportAsync(string path, NotebookDocument document, CancellationToken cancellationToken = default);
}

public interface IOfficePdfConverter
{
    Task ConvertAsync(string sourcePath, string outputPdfPath, CancellationToken cancellationToken = default);
}
