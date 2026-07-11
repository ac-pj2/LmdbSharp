// LmdbEntityStore: the self-contained demo backend (LMDB object database).
// Seeds a small forum so standalone mode has life in it.
using Lmdb.Objects;

namespace ConfigViews;

public sealed class LmdbEntityStore : IEntityStore
{
    private readonly Collection<EntityRecord> _records;
    private readonly object _writeGate = new();

    public LmdbEntityStore(Collection<EntityRecord> records)
    {
        _records = records;
        SeedIfEmpty();
    }

    public List<EntityRecord> LoadAll()
    {
        using var txn = _records.Database.BeginRead();
        return _records.Scan(txn).OrderBy(r => r.Id).ToList();
    }

    public EntityRecord CreateEntity(string entityType, string author, Dictionary<string, string> fields)
    {
        var prefix = entityType == "forum-thread" ? "THRD" : entityType == "forum-category" ? "CAT" : "";
        return Write(entityType, prefix, author, fields, "");
    }

    public EntityRecord CreateReply(string parentKey, string body, string author)
        => Write("comment", "", author, new() { ["body"] = body }, parentKey);

    public List<EntityRecord> FetchByKeys(IReadOnlyCollection<string> keys)
        => LoadAll().Where(r => keys.Contains(r.Key)).ToList();

    private EntityRecord Write(string entityType, string refPrefix, string author,
        Dictionary<string, string> fields, string parentKey)
    {
        lock (_writeGate)
        {
            using var txn = _records.Database.BeginWrite();
            var rec = new EntityRecord
            {
                EntityType = entityType,
                ParentKey = parentKey,
                AuthorName = author,
                CreatedAt = DateTime.UtcNow,
                Fields = fields,
            };
            _records.Insert(txn, rec);
            rec.Key = rec.Id.ToString();
            rec.Ref = refPrefix == "" ? $"#{rec.Id}" : $"{refPrefix}-{rec.Id:d4}";
            _records.Update(txn, rec);
            txn.Commit();
            return rec;
        }
    }

    private void SeedIfEmpty()
    {
        using (var read = _records.Database.BeginRead())
        {
            if (_records.Scan(read).Any()) return;
        }

        var general = CreateEntity("forum-category", "system", new() { ["name"] = "General" });
        var wins = CreateEntity("forum-category", "system", new() { ["name"] = "Wins" });
        var questions = CreateEntity("forum-category", "system", new() { ["name"] = "Questions" });

        var welcome = CreateEntity("forum-thread", "Coach Dana", new()
        {
            ["title"] = "Welcome to the community — introduce yourself!",
            ["body"] = "Tell us who you are and what you're working on.\n\nHouse rules: be kind, be specific, celebrate each other's wins.",
            ["category"] = general.Key,
            ["pinned"] = "true",
        });
        CreateReply(welcome.Key, "Hi all — Alice here, training for my first marathon.", "Alice");
        CreateReply(welcome.Key, "Ben, working on consistency more than intensity this year.", "Ben");

        var win = CreateEntity("forum-thread", "Alice", new()
        {
            ["title"] = "Hit my 10k goal this morning 🎉",
            ["body"] = "Six months of slow progress and it finally clicked. Splits in the reply.",
            ["category"] = wins.Key,
        });
        CreateReply(win.Key, "Huge! This is what the plan was building toward.", "Coach Dana");

        CreateEntity("forum-thread", "Ben", new()
        {
            ["title"] = "How do you handle rest-day guilt?",
            ["body"] = "I know rest matters but I feel like I'm losing progress. Tactics welcome.",
            ["category"] = questions.Key,
        });
        CreateEntity("forum-thread", "Coach Dana", new()
        {
            ["title"] = "This week's group call moved to Thursday",
            ["body"] = "Same time, same link. Recording will be posted as usual.",
            ["category"] = general.Key,
            ["pinned"] = "true",
            ["closed"] = "true",
        });
    }
}
