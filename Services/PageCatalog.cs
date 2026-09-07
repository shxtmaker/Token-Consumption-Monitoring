using TokenConsumptionMonitoring.Models;

namespace TokenConsumptionMonitoring.Services;

/// <summary>独占页面集合，向调用方提供隔离副本，仅在持久化成功后发布新修订。</summary>
public sealed class PageCatalog
{
    private readonly object _gate = new();
    private List<PageConfigRecord> _pages;
    private long _revision;
    public event Action<string>? Changed;

    public PageCatalog(IEnumerable<PageConfigRecord> pages)
    {
        _pages = pages.Select(Copy).ToList();
        foreach (var page in _pages) page.Revision = ++_revision;
    }

    public List<PageConfigRecord> Snapshot()
    {
        lock (_gate) return _pages.Select(Copy).ToList();
    }

    public void InvalidateSession(Func<PageConfigRecord, bool> affected)
    {
        List<string> changed = new();
        lock (_gate)
        {
            var generation = Guid.NewGuid().ToString("N");
            foreach (var page in _pages.Where(page => affected(Copy(page))))
            {
                page.Revision = ++_revision;
                page.SessionGeneration = generation;
                changed.Add(page.Id);
            }
        }
        foreach (var id in changed) NotifyChanged(id);
    }

    public bool IsCurrent(string id, long revision)
    {
        lock (_gate) return _pages.Any(page => page.Id == id && page.Revision == revision);
    }

    /// <summary>版本检查与发布使用同一锁，旧请求即使忽略取消也不能写入结果。</summary>
    public void Publish(PageConfigRecord page, Action action)
    {
        lock (_gate)
        {
            if (!_pages.Any(current => current.Id == page.Id && current.Revision == page.Revision))
                throw new OperationCanceledException("页面修订已失效");
            action();
        }
    }

    public PageConfigurationSaveResult Save(PageConfigRecord draft, long? expectedRevision,
        Func<PageConfigDocument, PageConfigurationSaveResult> persist)
        => Commit(draft.Id, expectedRevision, draft, persist);

    public PageConfigurationSaveResult Delete(string id, long expectedRevision,
        Func<PageConfigDocument, PageConfigurationSaveResult> persist)
        => Commit(id, expectedRevision, null, persist);

    private PageConfigurationSaveResult Commit(string id, long? expectedRevision, PageConfigRecord? draft,
        Func<PageConfigDocument, PageConfigurationSaveResult> persist)
    {
        lock (_gate)
        {
            var index = _pages.FindIndex(page => page.Id == id);
            if (expectedRevision is { } expected
                ? index < 0 || _pages[index].Revision != expected
                : index >= 0)
                return new(false, "页面已变化，请重新载入后保存");

            var next = _pages.Select(Copy).ToList();
            if (draft is null)
                next.RemoveAt(index);
            else
            {
                var saved = Copy(draft);
                saved.Revision = _revision + 1;
                if (index < 0) next.Add(saved);
                else next[index] = saved;
            }
            // 持久化入口不能取得目录内部记录的可变引用。
            var result = persist(new PageConfigDocument { Pages = next.Select(Copy).ToList() });
            if (!result.Succeeded) return result;
            _pages = next;
            _revision++;
        }
        // 提交已经完成；通知失败不能将成功保存报告为失败。
        NotifyChanged(id);
        return new(true);
    }

    private void NotifyChanged(string id)
    {
        foreach (var handler in Changed?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action<string>)handler)(id); }
            catch { Logger.Log("页面变更通知失败，配置已保存"); }
        }
    }

    public static PageConfigRecord Copy(PageConfigRecord page) => new()
    {
        Id = page.Id, Name = page.Name, BaseUrl = page.BaseUrl, Protocol = page.Protocol,
        CredentialRef = page.CredentialRef, SortOrder = page.SortOrder, Revision = page.Revision,
        SessionGeneration = page.SessionGeneration,
        ConfiguredModelHints = new(page.ConfiguredModelHints),
        EnabledCompatibilityMethods = new(page.EnabledCompatibilityMethods),
        Deprecated = page.Deprecated is { } old ? new DeprecatedPageSettings
        {
            AmountWarnCny = old.AmountWarnCny, AmountCriticalCny = old.AmountCriticalCny,
            TokenWarn = old.TokenWarn, TokenCritical = old.TokenCritical,
        } : null,
    };
}
