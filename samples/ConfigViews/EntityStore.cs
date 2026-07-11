// LMDB-backed entity store for the PoC (stands in for p2's PostgreSQL layer;
// a production integration would put an EF Core/REST adapter behind the same
// three methods). Seeds a small forum so the demo has life in it.
using Lmdb.Objects;

namespace ConfigViews;

public sealed class EntityStore
{
    private readonly Collection<EntityRecord> _records;
    private readonly object _writeGate = new();

    public EntityStore(Collection<EntityRecord> records)
    {
        _records = records;
        SeedIfEmpty();
    }

    public List<EntityRecord> LoadAll()
    {
        using var txn = _records.Database.BeginRead();
        return _records.Scan(txn).OrderBy(r => r.Id).ToList();
    }

    public EntityRecord Insert(string entityType, string refPrefix, string author,
        Dictionary<string, string> fields, long parentId = 0)
    {
        lock (_writeGate)
        {
            using var txn = _records.Database.BeginWrite();
            var rec = new EntityRecord
            {
                EntityType = entityType,
                ParentId = parentId,
                AuthorName = author,
                CreatedAt = DateTime.UtcNow,
                Fields = fields,
            };
            _records.Insert(txn, rec);
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

        var general = Insert("forum-category", "CAT", "system", new() { ["name"] = "General" });
        var wins = Insert("forum-category", "CAT", "system", new() { ["name"] = "Wins" });
        var questions = Insert("forum-category", "CAT", "system", new() { ["name"] = "Questions" });

        var welcome = Insert("forum-thread", "THRD", "Coach Dana", new()
        {
            ["title"] = "Welcome to the community — introduce yourself!",
            ["body"] = "Tell us who you are and what you're working on.\n\nHouse rules: be kind, be specific, celebrate each other's wins.",
            ["category"] = general.Id.ToString(),
            ["pinned"] = "true",
        });
        Insert("comment", "", "Alice", new() { ["body"] = "Hi all — Alice here, training for my first marathon." }, welcome.Id);
        Insert("comment", "", "Ben", new() { ["body"] = "Ben, working on consistency more than intensity this year." }, welcome.Id);

        var win = Insert("forum-thread", "THRD", "Alice", new()
        {
            ["title"] = "Hit my 10k goal this morning 🎉",
            ["body"] = "Six months of slow progress and it finally clicked. Splits in the reply.",
            ["category"] = wins.Id.ToString(),
        });
        Insert("comment", "", "Coach Dana", new() { ["body"] = "Huge! This is what the plan was building toward." }, win.Id);

        Insert("forum-thread", "THRD", "Ben", new()
        {
            ["title"] = "How do you handle rest-day guilt?",
            ["body"] = "I know rest matters but I feel like I'm losing progress. Tactics welcome.",
            ["category"] = questions.Id.ToString(),
        });
        Insert("forum-thread", "THRD", "Coach Dana", new()
        {
            ["title"] = "This week's group call moved to Thursday",
            ["body"] = "Same time, same link. Recording will be posted as usual.",
            ["category"] = general.Id.ToString(),
            ["pinned"] = "true",
            ["closed"] = "true",
        });
    }
}
