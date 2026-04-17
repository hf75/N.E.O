using Neo.App.WebApp.Services.Sessions;

namespace Neo.App.WebApp.Tests;

public class InMemorySessionStoreTests
{
    [Fact]
    public async Task RoundTrip_SaveLoadDelete()
    {
        var store = new InMemorySessionStore();
        Assert.Empty(await store.ListAsync());

        var s = new NeoSession { Name = "foo", Code = "class X {}" };
        await store.SaveAsync(s);

        var names = await store.ListAsync();
        Assert.Single(names);
        Assert.Equal("foo", names[0]);

        var loaded = await store.LoadAsync("foo");
        Assert.NotNull(loaded);
        Assert.Equal("class X {}", loaded!.Code);

        await store.DeleteAsync("foo");
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task LoadingMissing_ReturnsNull()
    {
        var store = new InMemorySessionStore();
        Assert.Null(await store.LoadAsync("missing"));
    }

    [Fact]
    public async Task Save_IsIdempotent()
    {
        var store = new InMemorySessionStore();
        var s1 = new NeoSession { Name = "x", Code = "v1" };
        var s2 = new NeoSession { Name = "x", Code = "v2" };
        await store.SaveAsync(s1);
        await store.SaveAsync(s2);

        var loaded = await store.LoadAsync("x");
        Assert.Equal("v2", loaded!.Code);
        Assert.Single(await store.ListAsync());
    }
}
