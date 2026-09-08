using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services;

public interface IPageCredentialStore
{
    bool TryRead(string target, out string? secret);
    void Write(string target, string secret);
    void Delete(string target);
}

public sealed class WindowsPageCredentialStore : IPageCredentialStore
{
    public bool TryRead(string target, out string? secret) => CredentialStore.TryReadSecret(target, out secret);
    public void Write(string target, string secret) => CredentialStore.SaveSecret(target, secret);
    public void Delete(string target) => CredentialStore.Delete(target);
}

/// <summary>串行提交页面与版本化凭据；只有 JSON 提交成功后目录才发布新版本。</summary>
public sealed class PageConfigurationCommands(
    PageCatalog catalog, IPageConfigurationPersistence persistence, IPageCredentialStore credentials)
{
    public string ReadApiKeyForEditing(PageConfigRecord page)
        => CredentialReference.IsSecretKey(page.CredentialRef.ResolveClass())
            && page.CredentialRef.Target is { Length: > 0 } target
            && credentials.TryRead(target, out var secret) ? secret ?? "" : "";

    public PageConfigurationSaveResult Save(PageConfigRecord draft, long? expectedRevision, string? newSecret)
    {
        var prepared = PageCatalog.Copy(draft);
        string? newTarget = null;
        if (newSecret is not null)
        {
            newTarget = AppIdentity.ApiKeyTarget(prepared.Id) + ".v." + Guid.NewGuid().ToString("N");
            var kind = prepared.CredentialRef.ResolveClass();
            prepared.CredentialRef = CredentialReference.SecretKey(newTarget,
                CredentialReference.IsSecretKey(kind) ? kind : CredentialClass.ApiKey);
        }
        return catalog.Save(prepared, expectedRevision, document =>
        {
            var validation = persistence.ValidateWrite(document);
            if (!validation.Succeeded) return validation;
            try
            {
                if (newTarget is not null) credentials.Write(newTarget, newSecret!);
                var saved = persistence.Save(document);
                if (!saved.Succeeded) Cleanup();
                return saved;
            }
            catch (Exception)
            {
                Cleanup();
                // 不将系统异常原文或凭据内容传入界面和日志。
                return new(false, "页面或凭据保存失败，原配置保持不变");
            }
        });

        void Cleanup()
        {
            if (newTarget is null) return;
            try { credentials.Delete(newTarget); }
            catch { Logger.Log("新凭据清理未完成，原有凭据保持不变"); }
        }
    }

    public PageConfigurationSaveResult Delete(PageConfigRecord page)
        => catalog.Delete(page.Id, page.Revision, persistence.Save);
}
