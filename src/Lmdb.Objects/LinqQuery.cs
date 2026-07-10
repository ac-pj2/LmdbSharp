// LINQ query provider for Collection<T>. Translates simple Where clauses into
// index-backed scans when possible, falling back to full-scan for unsupported predicates.
//
// Supported patterns (use indexed fields for best performance):
//   col.Query(txn).Where(x => x.Email == "alice@x.com")     -> FindBy
//   col.Query(txn).Where(x => x.Age >= 18 && x.Age < 65)    -> FindByRange
//   col.Query(txn).Where(x => x.Name.StartsWith("Al"))      -> FindByPrefix
//   col.Query(txn).Where(x => x.Email == "a@b.com").Take(10)
//
// Unsupported predicates fall back to a full collection scan with in-memory filtering.
using System.Linq.Expressions;
using Lmdb;

namespace Lmdb.Objects;

/// <summary>LINQ-queryable wrapper over a collection within a transaction.</summary>
public sealed class ObjectQuery<T> where T : class
{
    private readonly Collection<T> _collection;
    private readonly LmdbTransaction _txn;
    private Expression<Func<T, bool>>? _predicate;
    private int? _take;
    private int? _skip;

    internal ObjectQuery(Collection<T> collection, LmdbTransaction txn)
    {
        _collection = collection;
        _txn = txn;
    }

    public ObjectQuery<T> Where(Expression<Func<T, bool>> predicate)
    {
        _predicate = _predicate == null
            ? predicate
            : Expression.Lambda<Func<T, bool>>(
                Expression.AndAlso(_predicate.Body, predicate.Body),
                _predicate.Parameters);
        return this;
    }

    public ObjectQuery<T> Take(int count) { _take = count; return this; }
    public ObjectQuery<T> Skip(int count) { _skip = count; return this; }

    private string? _orderByField;
    private bool _descending;

    /// <summary>Order results by an indexed field. Uses the index for ordered iteration
    /// when possible (forward or reverse scan). Falls back to in-memory sorting otherwise.</summary>
    public ObjectQuery<T> OrderBy<TField>(Expression<Func<T, TField>> selector)
    {
        if (selector.Body is MemberExpression me)
            _orderByField = me.Member.Name;
        _descending = false;
        return this;
    }

    public ObjectQuery<T> OrderByDescending<TField>(Expression<Func<T, TField>> selector)
    {
        if (selector.Body is MemberExpression me)
            _orderByField = me.Member.Name;
        _descending = true;
        return this;
    }

    public List<T> ToList() => Enumerate().ToList();
    public T? FirstOrDefault() => Enumerate().FirstOrDefault();

    public long Count()
    {
        if (_predicate == null && _skip == null && _take == null)
            return _collection.Count(_txn);
        return Enumerate().LongCount();
    }

    public IEnumerable<T> Enumerate()
    {
        IEnumerable<T>? results = null;

        // Try to use an index for the predicate.
        if (_predicate != null)
        {
            results = TryIndexScan();
        }

        // If no index path was found, try an ordered index scan (if OrderBy was used).
        if (results == null && _orderByField != null && _predicate == null)
        {
            results = TryOrderedScan();
        }

        // Fallback: full scan with in-memory predicate evaluation.
        if (results == null)
        {
            var compiled = _predicate?.Compile();
            results = _collection.Scan(_txn);
            if (compiled != null) results = results.Where(compiled);
        }

        // Apply in-memory ordering if we didn't use an ordered index scan.
        if (_orderByField != null && results is not OrderedScanMarker)
        {
            results = ApplyOrderBy(results);
        }

        results = ApplySkipTake(results);
        foreach (var item in results) yield return item;
    }

    /// <summary>Marker type to signal that results are already ordered (from index scan).</summary>
    private sealed class OrderedScanMarker : List<T> { }

    /// <summary>Scan an index in sorted order (forward or reverse).</summary>
    private IEnumerable<T>? TryOrderedScan()
    {
        var indexDbName = $"{_collection.Name}:{_orderByField}";
        Lmdb.LmdbDatabase? indexDb;
        try { indexDb = _txn.GetCachedDb(indexDbName) ?? _txn.OpenDatabase(indexDbName); }
        catch (Lmdb.LmdbException) { return null; }  // index doesn't exist

        var ordered = new OrderedScanMarker();
        using var cur = _txn.CreateCursor(indexDb);
        var op = _descending ? Lmdb.CursorOp.Last : Lmdb.CursorOp.First;
        if (!cur.TryGet(op, default, out _, out var pkData))
            return ordered;

        do
        {
            object pk = Lmdb.Objects.KeyEncoding.Decode(pkData, _collection.KeyType);
            var obj = _collection.Get(_txn, pk);
            if (obj != null) ordered.Add(obj);
        }
        while (cur.TryGet(_descending ? Lmdb.CursorOp.Prev : Lmdb.CursorOp.Next, default, out _, out pkData));

        return ordered;
    }

    private IEnumerable<T> ApplyOrderBy(IEnumerable<T> source)
    {
        var param = Expression.Parameter(typeof(T), "x");
        var prop = Expression.Property(param, _orderByField!);
        var lambda = Expression.Lambda(prop, param).Compile();
        return _descending
            ? source.OrderByDescending(x => lambda.DynamicInvoke(x))
            : source.OrderBy(x => lambda.DynamicInvoke(x));
    }

    private IEnumerable<T>? TryIndexScan()
    {
        if (_predicate!.Body is not BinaryExpression binary) return null;

        if (binary.NodeType == ExpressionType.AndAlso)
            return TryRangeScan(binary);

        if (binary.NodeType == ExpressionType.Equal)
        {
            var (field, value) = ExtractFieldAndValue(binary);
            if (field != null && value != null)
                return ApplySkipTake(_collection.FindAllBy(_txn, field, value));
        }

        if (binary.NodeType is ExpressionType.GreaterThanOrEqual or ExpressionType.GreaterThan
            or ExpressionType.LessThanOrEqual or ExpressionType.LessThan)
        {
            return TryComparisonScan(binary);
        }

        if (_predicate.Body is MethodCallExpression mce
            && mce.Method.Name == "StartsWith"
            && mce.Object is MemberExpression me
            && mce.Arguments[0] is ConstantExpression ce && ce.Value is string prefix)
        {
            return ApplySkipTake(_collection.FindByPrefix(_txn, me.Member.Name, prefix));
        }

        return null;
    }

    private IEnumerable<T>? TryRangeScan(BinaryExpression andAlso)
    {
        if (andAlso.Left is not BinaryExpression left || andAlso.Right is not BinaryExpression right)
            return null;

        var (leftField, leftVal, leftOp) = ExtractFieldOpValue(left);
        var (rightField, rightVal, rightOp) = ExtractFieldOpValue(right);

        if (leftField == null || rightField == null || leftField != rightField)
            return null;

        string fieldName = leftField!;
        object? from = null, to = null;

        if (leftOp == ExpressionType.GreaterThanOrEqual) from = leftVal;
        else if (leftOp == ExpressionType.GreaterThan) from = leftVal;
        if (rightOp == ExpressionType.LessThan) to = rightVal;
        else if (rightOp == ExpressionType.LessThanOrEqual) to = rightVal;

        if (from == null && rightOp == ExpressionType.GreaterThanOrEqual)
        { from = rightVal; to = leftVal; }
        if (from == null) return null;

        return ApplySkipTake(_collection.FindByRange(_txn, fieldName, from, to));
    }

    private IEnumerable<T>? TryComparisonScan(BinaryExpression binary)
    {
        var (field, val, op) = ExtractFieldOpValue(binary);
        if (field == null || val == null) return null;

        object? from = op is ExpressionType.GreaterThanOrEqual or ExpressionType.GreaterThan ? val : null;
        object? to = op is ExpressionType.LessThan or ExpressionType.LessThanOrEqual ? val : null;

        return ApplySkipTake(_collection.FindByRange(_txn, field, from, to));
    }

    private IEnumerable<T> ApplySkipTake(IEnumerable<T> source)
    {
        if (_skip != null) source = source.Skip(_skip.Value);
        if (_take != null) source = source.Take(_take.Value);
        return source;
    }

    // Expression extraction helpers

    private static (string? field, object? value) ExtractFieldAndValue(BinaryExpression binary)
    {
        if (ExtractMember(binary.Left) is string f1 && ExtractConstant(binary.Right) is object v1)
            return (f1, v1);
        if (ExtractMember(binary.Right) is string f2 && ExtractConstant(binary.Left) is object v2)
            return (f2, v2);
        return (null, null);
    }

    private static (string? field, object? value, ExpressionType op) ExtractFieldOpValue(BinaryExpression binary)
    {
        if (ExtractMember(binary.Left) is string f1 && ExtractConstant(binary.Right) is object v1)
            return (f1, v1, binary.NodeType);
        if (ExtractMember(binary.Right) is string f2 && ExtractConstant(binary.Left) is object v2)
            return (f2, v2, binary.NodeType);
        return (null, null, binary.NodeType);
    }

    private static string? ExtractMember(Expression expr)
        => expr is MemberExpression me ? me.Member.Name : null;

    private static object? ExtractConstant(Expression expr)
        => expr is ConstantExpression ce ? ce.Value :
           expr is UnaryExpression { NodeType: ExpressionType.Convert, Operand: ConstantExpression ce2 } ? ce2.Value : null;
}

/// <summary>Extension methods to create queries.</summary>
public static class QueryExtensions
{
    /// <summary>Start a LINQ query over a collection within a transaction.</summary>
    public static ObjectQuery<T> Query<T>(this Collection<T> collection, LmdbTransaction txn) where T : class
        => new(collection, txn);
}
