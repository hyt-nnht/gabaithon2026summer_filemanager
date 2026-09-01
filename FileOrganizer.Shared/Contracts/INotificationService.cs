using FileOrganizer.Shared.Models;

namespace FileOrganizer.Shared.Contracts;

public interface INotificationService
{
    Task ShowToastAsync(string title, string message, ToastType type, CancellationToken ct = default);
}
