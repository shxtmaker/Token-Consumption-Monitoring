using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Services;
using Xunit;

namespace TokenConsumptionMonitoring.Tests;

public sealed class PageCatalogTests
{
    [Fact]
    public void SessionChange_InvalidatesOnlyAffectedPagesWithoutPersistingGeneration()
    {
        var catalog = new PageCatalog(new[] { new PageConfigRecord { Id = "session" }, new PageConfigRecord { Id = "key" } });
        var before = catalog.Snapshot();
        var notifications = new List<string>();
        catalog.Changed += notifications.Add;
        catalog.InvalidateSession(page => page.Id == "session");
        Assert.False(catalog.IsCurrent("session", before.Single(page => page.Id == "session").Revision));
        Assert.True(catalog.IsCurrent("key", before.Single(page => page.Id == "key").Revision));
        var changed = catalog.Snapshot().Single(page => page.Id == "session");
        Assert.NotEmpty(changed.SessionGeneration);
        Assert.Equal(new[] { "session" }, notifications);
        var json = System.Text.Json.JsonSerializer.Serialize(changed);
        Assert.DoesNotContain("SessionGeneration", json);
        Assert.DoesNotContain(changed.SessionGeneration, json);
    }

    [Fact]
    public void Snapshot_CannotMutateOwnedRecordsOrNestedCollections()
    {
        var source = new PageConfigRecord { Name = "original", ConfiguredModelHints = new() { "model" } };
        var catalog = new PageCatalog(new[] { source });
        source.Name = "external";
        var snapshot = catalog.Snapshot();
        snapshot[0].Name = "changed";
        snapshot[0].ConfiguredModelHints.Clear();
        snapshot.Clear();
        Assert.Equal("original", catalog.Snapshot().Single().Name);
        Assert.Equal("model", catalog.Snapshot().Single().ConfiguredModelHints.Single());
    }

    [Fact]
    public void FailedSave_DoesNotPublishDraftOrAdvanceRevision()
    {
        var catalog = new PageCatalog(new[] { new PageConfigRecord { Name = "original" } });
        var draft = catalog.Snapshot().Single();
        draft.Name = "draft";
        Assert.False(catalog.Save(draft, draft.Revision, _ => new(false)).Succeeded);
        Assert.Equal("original", catalog.Snapshot().Single().Name);
        Assert.True(catalog.IsCurrent(draft.Id, draft.Revision));
    }

    [Fact]
    public void Commit_InvalidatesOldRevisionAndRejectsStaleEditorBeforePersistence()
    {
        var catalog = new PageCatalog(new[] { new PageConfigRecord() });
        var draft = catalog.Snapshot().Single();
        Assert.True(catalog.Save(draft, draft.Revision, _ => new(true)).Succeeded);
        Assert.False(catalog.IsCurrent(draft.Id, draft.Revision));
        var called = false;
        Assert.False(catalog.Save(draft, draft.Revision, _ => { called = true; return new(true); }).Succeeded);
        Assert.False(called);
        var saved = catalog.Snapshot().Single();
        Assert.True(catalog.Delete(saved.Id, saved.Revision, _ => new(true)).Succeeded);
        Assert.False(catalog.IsCurrent(saved.Id, saved.Revision));
    }
}
