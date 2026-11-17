using System.Xml.Linq;
using IPhoneMirror.App.Services.ExplorerMapping;

internal static class ExplorerWebDavLocksTests
{
    internal static void Run()
    {
        var time = new LockClock();
        var dav = (XNamespace)"DAV:";
        var origin = new Uri("http://127.0.0.1:45678/session/");
        ExplorerWebDavLocks NewLocks() => new(path => origin.AbsoluteUri + path,
            href => Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme == origin.Scheme &&
                uri.Authority == origin.Authority && uri.AbsolutePath.StartsWith(origin.AbsolutePath, StringComparison.Ordinal)
                    ? Uri.UnescapeDataString(uri.AbsolutePath[origin.AbsolutePath.Length..]).TrimEnd('/') : null, time);
        var locks = NewLocks();
        var owner = new XElement(dav + "owner", new XElement(dav + "href", "phone-editor"));
        var file = locks.Acquire("folder/new.txt", false, owner, 600);
        owner.Value = "changed";
        Require(locks.Discovery("folder/new.txt").Descendants(dav + "owner").Single().Value == "phone-editor",
            "Lock owner must be detached from its caller's mutable XML.");
        Require(!locks.ValidateWrite([file.Path], null, file.Path) &&
            locks.ValidateWrite([file.Path], If(file.Token), file.Path), "An exclusive lock did not require its current token.");
        Require(!locks.ValidateWrite([file.Path], "(Not <" + file.Token + ">)", file.Path) &&
            !locks.ValidateWrite([file.Path], "([\"etag\"])", file.Path), "Unsupported If conditions must fail closed.");
        Require(!locks.ValidateWrite([file.Path], $"<{origin}unrelated> {If(file.Token)}", file.Path),
            "A valid token tagged to an unrelated resource authorized a write.");
        var invalid = "opaquelocktoken:" + Guid.NewGuid().ToString("D");
        Require(locks.ValidateWrite([file.Path], If(invalid) + " " + If(file.Token), file.Path) &&
            !locks.ValidateWrite([file.Path], "(<" + file.Token + "> <" + invalid + ">)", file.Path),
            "If alternatives and conjunctive token lists were conflated.");
        Require(!locks.ValidateWrite([file.Path], $"<http://example.test/session/{file.Path}> {If(file.Token)}", file.Path),
            "A foreign resource tag authorized a local mapping write.");
        Require(locks.TryGetRefreshToken(If(file.Token), file.Path, out var refresh) && refresh == file.Token &&
            !locks.TryGetRefreshToken(If(file.Token), "folder", out _), "Refresh must identify the lock at the request resource.");
        var normalized = "OPAQUELOCKTOKEN:" + file.Token["opaquelocktoken:".Length..].ToUpperInvariant();
        Require(locks.Refresh(file.Path, normalized, 5000) is { } renewed &&
            renewed.ExpiresAt == time.GetUtcNow().AddSeconds(1800), "Refresh did not normalize or cap the lease.");
        Expect(423, () => locks.Acquire(file.Path, false, null, 600));
        Expect(423, () => locks.Acquire("folder", true, null, 600));
        var shallow = locks.Acquire("folder", false, null, 600);
        Require(locks.ValidateWrite(["folder/other.txt"], null, "folder/other.txt"), "A depth-zero directory lock incorrectly covered a child.");
        Require(!locks.ValidateWrite(["folder"], If(shallow.Token), "folder", includeDescendants: true),
            "Deleting a parent bypassed its child's lock.");
        var recursiveIf = $"<{origin}folder> {If(shallow.Token)} <{origin}{file.Path}> {If(file.Token)}";
        Require(locks.ValidateWrite(["folder"], recursiveIf, "folder", includeDescendants: true),
            "A recursive write with all properly tagged lock tokens was rejected.");
        Require(locks.Release("folder", shallow.Token) && !locks.Release("different", file.Token),
            "Unlock changed a different resource's lease.");
        Require(locks.Release(file.Path, file.Token), "Unlock did not remove its exact lease.");
        var deep = locks.Acquire("folder", true, null, 1);
        Require(locks.Discovery("folder/a/b").Descendants(dav + "lockroot").Single().Value == origin + "folder" &&
            !locks.ValidateWrite(["folder/a/b"], null, "folder/a/b") &&
            locks.ValidateWrite(["folder/a/b"], If(deep.Token), "folder/a/b"),
            "A deep lock did not protect descendants or report its real root.");
        Expect(423, () => locks.Acquire("folder/other", false, null, 600));
        time.Advance(TimeSpan.FromSeconds(1));
        Require(!locks.Discovery("folder").Elements().Any() && locks.Refresh("folder", deep.Token, 600) is null &&
            !locks.Release("folder", deep.Token), "Expired tokens remained valid.");
        var replacement = locks.Acquire("folder", true, null, 600);
        Require(replacement.Token != deep.Token && !locks.ValidateWrite(["folder"], If(deep.Token), "folder"),
            "A new lease reused an expired authorization token.");
        Require(!NewLocks().ValidateWrite(["folder"], If(replacement.Token), "folder"), "A token crossed mapping-session boundaries.");
        locks = NewLocks();
        for (var index = 0; index < 128; index++) locks.Acquire("item" + index, false, null, 1);
        Expect(507, () => locks.Acquire("overflow", false, null, 600));
        time.Advance(TimeSpan.FromSeconds(2));
        locks.Acquire("overflow", false, null, 600);
        locks = NewLocks();
        var ancestor = locks.Acquire("tree", false, null, 600);
        var removedChild = locks.Acquire("tree/removed/child", false, null, 600);
        var retainedSibling = locks.Acquire("tree/retained", false, null, 600);
        locks.RemoveSubtree("tree/removed");
        Require(locks.Refresh(removedChild.Path, removedChild.Token, 600) is null &&
            locks.Refresh(ancestor.Path, ancestor.Token, 600) is not null &&
            locks.Refresh(retainedSibling.Path, retainedSibling.Token, 600) is not null,
            "Namespace cleanup removed ancestor/sibling locks or left a deleted child's lock alive.");
        locks.Acquire(removedChild.Path, false, null, 600);
        locks.RemoveSubtree("tree");
        Require(!locks.Discovery("tree").Elements().Any() &&
            locks.ValidateWrite(["tree/retained"], null, "tree/retained"),
            "Removing a source subtree left ghost lock tokens blocking namespace reuse.");
        Console.WriteLine("PASS Explorer WebDAV locks: exclusive scopes, token conditions, recursive writes, refresh/unlock, expiration, ownership copies and session/capacity isolation.");
    }

    private static string If(string token) => "(<" + token + ">)";
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Expect(int status, Action action)
    {
        try { action(); }
        catch (ExplorerWebDavLockException error) when (error.Status == status) { return; }
        throw new Exception("Expected lock status " + status);
    }
    private sealed class LockClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan delta) => _now += delta;
    }
}
