using GoreBox.Models;

namespace GoreBox.Services;

/// <summary>Диалоги приложения (реализуются во view-слое).</summary>
public interface IDialogService
{
    /// <summary>Редактор профиля. true — если пользователь сохранил.</summary>
    bool EditProfile(ProxyProfile profile, LatencyService latency);

    /// <summary>Импорт: возвращает разобранные профили (или пустой список, если отменили).</summary>
    List<ProxyProfile> ImportProfiles(string? prefill = null);

    string? PromptText(string title, string caption, string initial = "");

    bool Confirm(string title, string text, string okText = "Удалить", bool danger = true);

    void Info(string title, string text);

    void Error(string title, string text);

    /// <summary>Выбор приложений: из запущенных процессов и/или по .exe. Возвращает список путей к .exe.</summary>
    List<RunningApp> PickApplications();

    string? OpenFile(string filter, string? initialDirectory = null);

    string? SaveFile(string filter, string defaultName);

    /// <summary>Предложить перезапуск с правами администратора.</summary>
    bool ConfirmElevate(string title, string text);

    void ShowCoreLog(string logText);

    /// <summary>Выбор папки. null — если пользователь отменил выбор.</summary>
    string? PickFolder(string description, string? initialDirectory = null);
}
