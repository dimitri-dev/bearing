using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Bearing.Core.Data;

namespace Bearing.Data.Search;

/// <summary>
/// Runs Elasticsearch SQL / ES|QL and OpenSearch SQL / PPL statements over the connection's one HTTP door
/// (<see cref="SearchConnectionFactory.SendAsync"/>), a statement at a time (spec, Behaviour 4, 5 and 9). The
/// sibling of <c>DynamoDbQueryExecutor</c>, the other engine whose reads continue by token: a failure is returned
/// as an error result, the caller's own cancellation is thrown.
/// <para>
/// <b>One statement, one request, one endpoint.</b> A script is split by <see cref="SearchBatch"/> — the split the
/// write guard's <c>SearchScan</c> is pinned to — and each statement is sent alone, without its <c>;</c>, to the
/// endpoint its language selects (<see cref="SearchProtocol.EndpointFor"/>). The run stops at the first failure;
/// the statements after it are not sent. A bare <c>;</c> sends nothing but keeps its number, so
/// <see cref="QueryResult.StatementIndex"/> is the statement's place in the editor's split.
/// </para>
/// <para>
/// <b>Paging.</b> Only the SQL endpoints page: the first request asks for <see cref="QueryOptions.MaxRows"/> rows
/// (clamped by <see cref="SearchProtocol.FetchSize"/>) — on OpenSearch only for a plain select
/// (<see cref="SearchProtocol.AsksForPages"/>: there <c>fetch_size</c> hands every other shape to the legacy engine,
/// which mislabels column types), so any other OpenSearch SQL statement is answered whole, capped by the cluster's
/// <c>plugins.query.size_limit</c> — and a <c>cursor</c> in the answer becomes the result's
/// <see cref="QueryResult.Continuation"/> (<see cref="SearchContinuation"/>, which holds the cursor and the column
/// layout, never the statement). Continuing sends that cursor alone, so the statement is never run twice. ES|QL,
/// PPL and a SQL answer without a cursor are returned <b>whole</b>: the server has no next page to offer, so
/// cutting the answer at <see cref="QueryOptions.MaxRows"/> would only hide rows that cannot be fetched again
/// (a host that must hold a cap trims, and says so).
/// </para>
/// <para>
/// <b>What the server said about a result goes to <see cref="QueryResult.Notice"/>, never
/// <see cref="QueryResult.Message"/></b>, which stays null on every result with columns — on a result that
/// continues, both hosts read <c>Message</c> as "the request budget cut this page short", and these engines have
/// no budget.
/// </para>
/// <para>
/// <b>The statement timeout is ours</b> (§1.9a, the SQL Server arrangement): a client-side cancel of the HTTP
/// request after <see cref="SearchConnectionFactory.StatementTimeoutSeconds"/>. Only when that clock fired — and
/// the caller's did not — is a failure worded as the timeout; a server's own timeout error is the server's.
/// </para>
/// <para>
/// <b>Nothing is kept between calls</b> except what travels in the continuation the caller hands back; a cursor
/// left unread is left to expire on the server.
/// </para>
/// </summary>
public sealed class SearchQueryExecutor : IQueryExecutor
{
    internal const string NothingToRun = "There is no statement to run.";

    private readonly SearchConnectionFactory _factory;

    public SearchQueryExecutor(SearchConnectionFactory factory) => _factory = factory;

    private SearchEngine Engine => _factory.Engine;

    private string EngineName => SearchErrors.EngineName(_factory.Engine);

    public async Task<IReadOnlyList<QueryResult>> ExecuteAsync(string sql, QueryOptions options, CancellationToken ct)
    {
        if (options.Parameters.Count > 0)
        {
            return [Failure(TimeSpan.Zero, new QueryError(VariablesUnavailable(Engine), null, null))];
        }

        var statements = SearchBatch.Statements(Engine, sql);
        if (statements.All(s => s.IsEmpty))
        {
            return [Failure(TimeSpan.Zero, new QueryError(NothingToRun, null, null))];
        }

        var results = new List<QueryResult>(statements.Count);

        for (var i = 0; i < statements.Count; i++)
        {
            // A bare ';' has nothing to send; it still counts, so statement N is the editor's statement N.
            if (statements[i].IsEmpty) continue;

            var result = await RunStatementAsync(statements[i], i, options.MaxRows, ct).ConfigureAwait(false);
            results.Add(result with { StatementIndex = i });

            // Each statement is its own request, so the ones before a failure have run; the ones after it are not
            // sent — a batch stops at its first error, as on the other engines.
            if (!result.Success) break;
        }

        return results;
    }

    /// <summary>
    /// The page after the one that carried <paramref name="continuation"/>: the cursor alone is sent, to the endpoint
    /// that issued it — never the statement. <paramref name="maxRows"/> is not sent: neither server lets a cursor
    /// change its page size, so the page is the size the first request asked for (the interface's "about").
    /// </summary>
    public async Task<QueryResult> ExecuteContinuationAsync(string continuation, int maxRows, CancellationToken ct)
    {
        SearchContinuation state;

        try
        {
            state = SearchContinuation.Decode(continuation);
        }
        catch (SearchContinuationException e)
        {
            return Failure(TimeSpan.Zero, new QueryError(e.Message, null, null));
        }

        // A token another engine's connection issued would send its cursor to the wrong server.
        if (state.Engine != Engine)
        {
            return Failure(TimeSpan.Zero, new QueryError(new SearchContinuationException().Message, null, null))
                with { StatementIndex = state.StatementIndex };
        }

        var endpoint = state.Endpoint;
        var sw = Stopwatch.StartNew();
        using var deadline = Deadline(ct);

        try
        {
            var response = await SendCursorAsync(state, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccess)
            {
                return Failure(sw.Elapsed, CursorRejected(state.Cursor, response)) with { StatementIndex = state.StatementIndex };
            }

            var page = SearchProtocol.ParseContinuationPage(endpoint, response, state.Layout.Columns.Count);
            var rows = SearchValues.LaterPage(state.Layout, page.Rows);
            var next = page.Cursor is { } cursor ? (state with { Cursor = cursor }).Encode() : null;

            return new QueryResult(
                state.Layout.Descriptors(), rows, rows.Count, sw.Elapsed, Message: null, Error: null,
                Truncated: next is not null)
            {
                StatementIndex = state.StatementIndex,
                Continuation = next,
                Notice = page.Notice,
            };
        }
        catch (Exception e) when (ct.IsCancellationRequested && e is not OperationCanceledException)
        {
            throw new OperationCanceledException("The statement was canceled.", e, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return Failure(sw.Elapsed, ErrorFor(e, deadline)) with { StatementIndex = state.StatementIndex };
        }
    }

    /// <summary>
    /// One statement from the start, read to the end — following the cursor on SQL — or to
    /// <see cref="QueryOptions.MaxRows"/>, which is exact here: the rows past it are dropped and the tail batch says
    /// <see cref="RowBatch.Truncated"/>. Kept for the contract's sake: the App continues these results through
    /// <see cref="ExecuteContinuationAsync"/>. Failures are thrown, as the interface says — the connection's timeout
    /// as a <see cref="TimeoutException"/> naming it, a server error as a <see cref="SearchServerException"/> — and
    /// the connection's timeout covers the whole stream. A cursor left when the cap is reached is left to expire.
    /// </summary>
    public async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        string sql, QueryOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        if (options.Parameters.Count > 0)
        {
            throw new NotSupportedException(VariablesUnavailable(Engine));
        }

        var statements = SearchBatch.Statements(Engine, sql).Where(s => !s.IsEmpty).ToList();
        if (statements.Count != 1)
        {
            throw new NotSupportedException(
                $"A streamed read runs exactly one statement; this text holds {statements.Count}.");
        }

        var statement = statements[0];
        var endpoint = SearchProtocol.EndpointFor(Engine, statement.Language);
        var batchSize = Math.Max(1, options.BatchRows);
        var max = options.MaxRows is { } cap ? Math.Max(0, cap) : (int?)null;
        using var deadline = Deadline(ct);

        var body = SearchProtocol.FirstPageBody(endpoint, statement.Text, Math.Min(max ?? batchSize, batchSize));
        var response = await StreamSendAsync(endpoint, body, deadline, ct).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            throw new SearchServerException(response.StatusCode, StatementError(response, endpoint, statement));
        }

        var first = SearchProtocol.ParseFirstPage(endpoint, response);

        // No result shape: nothing to stream (RowBatch's contract), but the statement has run.
        if (first.Columns is not { Count: > 0 } declared) yield break;

        var (layout, firstRows) = SearchValues.FirstPage(declared, first.Rows);
        var columns = layout.Descriptors();
        var pending = new List<object?[]>(firstRows);
        var cursor = endpoint.Pages ? first.Cursor : null;
        var yielded = 0;
        var trimmed = false;

        while (true)
        {
            // The cap is exact on a stream: what passes it is dropped, and the tail says the read was cut.
            if (max is { } most && yielded + pending.Count > most)
            {
                pending.RemoveRange(most - yielded, yielded + pending.Count - most);
                trimmed = true;
            }

            while (pending.Count >= batchSize)
            {
                var chunk = pending.GetRange(0, batchSize);
                pending.RemoveRange(0, batchSize);
                yielded += chunk.Count;

                yield return new RowBatch(columns, chunk, Truncated: false);
            }

            if (cursor is null || (max is { } full && yielded + pending.Count >= full)) break;

            var next = await StreamSendAsync(endpoint, SearchProtocol.ContinuationBody(endpoint, cursor), deadline, ct)
                .ConfigureAwait(false);
            if (!next.IsSuccess)
            {
                throw new SearchServerException(next.StatusCode, CursorRejected(cursor, next));
            }

            var page = SearchProtocol.ParseContinuationPage(endpoint, next, layout.Columns.Count);
            pending.AddRange(SearchValues.LaterPage(layout, page.Rows));
            cursor = page.Cursor;
        }

        // The tail, always (§5.7). A cursor left over — or rows the cap dropped — means the cap stopped the read,
        // not the result's end.
        yield return new RowBatch(columns, pending, Truncated: cursor is not null || trimmed);
    }

    public Task<QueryResult> ExecutePageAsync(
        string pageSql, IReadOnlyList<QueryParameter> parameters, CancellationToken ct)
        => Task.FromResult(Failure(TimeSpan.Zero, new QueryError(OffsetPagingUnavailable(Engine), null, null)));

    /// <summary>
    /// Null — the interface's "this shape cannot be counted", which the App words as "Count unavailable for this
    /// query on this engine". Nothing is sent: neither SQL endpoint counts a statement's rows short of reading
    /// them, and ES|QL / PPL have no wrapper the caller could shape.
    /// </summary>
    public Task<long?> CountAsync(string countSql, IReadOnlyList<QueryParameter> parameters, CancellationToken ct)
        => Task.FromResult<long?>(null);

    public Task<IReadOnlyList<QueryResult>> ExecuteWriteAsync(IReadOnlyList<SqlWriteCommand> commands, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<QueryResult>>(
            [Failure(TimeSpan.Zero, new QueryError(GeneratedWritesUnavailable(Engine), null, null))]);

    // ---- sentences ---------------------------------------------------------------------------------

    internal static string OffsetPagingUnavailable(SearchEngine engine)
        => $"Paging by offset is not available on {SearchErrors.EngineName(engine)}: a SQL result continues from the "
           + "server's cursor, and an ES|QL or PPL result arrives whole.";

    internal static string GeneratedWritesUnavailable(SearchEngine engine)
        => $"Saving edited cells is not available on {SearchErrors.EngineName(engine)} connections.";

    internal static string VariablesUnavailable(SearchEngine engine)
        => $"Script variables are not available on {SearchErrors.EngineName(engine)} connections; the statement was not sent.";

    /// <summary>How long the server keeps a cursor between pages — the setting Bearing sends on Elasticsearch
    /// (<see cref="SearchProtocol.PageTimeout"/>), the cluster's own on OpenSearch.</summary>
    internal static string CursorLifetime(SearchEngine engine) => engine == SearchEngine.Elasticsearch
        ? "Elasticsearch keeps a cursor for 5 minutes after its last page was read."
        : "OpenSearch keeps a cursor for the cluster's plugins.sql.cursor.keep_alive after its last page was read "
          + "(1 minute by default).";

    /// <summary>
    /// The sentence for a cursor request the server answered with an error. It says what was observed — the server
    /// did not take the cursor back — and why that usually happens, that the statement was not run again and how to
    /// read on; then the server's own reason, scrubbed, with the cursor cut out of it (§1.3: never shown).
    /// </summary>
    internal static string CursorRejectedSentence(SearchEngine engine, int status, string reason)
    {
        var name = SearchErrors.EngineName(engine);

        return $"{name} no longer accepts this result's cursor (HTTP {status}), so no more rows were read. "
               + $"{CursorLifetime(engine)} The statement was not run again: run it again to read the result from the "
               + $"start. {name} said: {reason}";
    }

    /// <summary>A statement answered with no columns — not a shape these read-only languages produce, but
    /// OpenSearch 2.x's opt-in <c>DELETE</c> can. Says what came back and claims nothing more.</summary>
    internal static string NoColumnsSentence(SearchEngine engine, string language, int rows)
        => $"{language} statement sent; {SearchErrors.EngineName(engine)} answered with no columns"
           + (rows == 0 ? " and no rows." : $" and {rows} empty row{(rows == 1 ? "" : "s")}.");

    // ---- one statement -----------------------------------------------------------------------------

    private async Task<QueryResult> RunStatementAsync(
        SearchBatchStatement statement, int index, int? maxRows, CancellationToken ct)
    {
        var endpoint = SearchProtocol.EndpointFor(Engine, statement.Language);
        var sw = Stopwatch.StartNew();
        using var deadline = Deadline(ct);

        try
        {
            var body = SearchProtocol.FirstPageBody(endpoint, statement.Text, maxRows);
            var response = await _factory
                .SendAsync(HttpMethod.Post, endpoint.Path, body, SearchConnectionFactory.DefaultResponseLimitBytes, deadline.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccess)
            {
                return Failure(sw.Elapsed, StatementError(response, endpoint, statement));
            }

            var page = SearchProtocol.ParseFirstPage(endpoint, response);

            return FirstPageResult(endpoint, statement, page, index, sw);
        }
        catch (Exception e) when (ct.IsCancellationRequested && e is not OperationCanceledException)
        {
            throw new OperationCanceledException("The statement was canceled.", e, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return Failure(sw.Elapsed, ErrorFor(e, deadline));
        }
    }

    private QueryResult FirstPageResult(
        SearchEndpoint endpoint, SearchBatchStatement statement, SearchPage page, int index, Stopwatch sw)
    {
        if (page.Columns is not { Count: > 0 } declared)
        {
            var message = NoColumnsSentence(Engine, endpoint.LanguageName, page.Rows.Count);

            // Message is what a result without columns shows, so the server's notice rides along in it.
            return new QueryResult(
                [], [], 0, sw.Elapsed,
                page.Notice is { } said ? $"{message} {EngineName} said: {said}" : message,
                Error: null, Truncated: false)
            {
                StatementIndex = index,
            };
        }

        var (layout, rows) = SearchValues.FirstPage(declared, page.Rows);

        // Only a SQL endpoint hands out a cursor (the protocol drops one anywhere else); no cursor, no more rows.
        var continuation = endpoint.Pages && page.Cursor is { } cursor
            ? new SearchContinuation(Engine, statement.Language, cursor, layout, index).Encode()
            : null;

        return new QueryResult(
            layout.Descriptors(), rows, rows.Count, sw.Elapsed, Message: null, Error: null,
            Truncated: continuation is not null)
        {
            StatementIndex = index,
            Continuation = continuation,
            Notice = page.Notice,
        };
    }

    /// <summary>A non-2xx answer to a statement, led by the language it was sent as ("ES|QL: line 1:15: …") so a
    /// statement sent to the other endpoint is recognisable. The server's <c>line L:C</c> is a position in the text
    /// that was sent; the editor's caret wants one in the script, so the statement's start is added.</summary>
    private static QueryError StatementError(SearchResponse response, SearchEndpoint endpoint, SearchBatchStatement statement)
    {
        var error = SearchErrors.ToQueryError(response.StatusCode, response.Body, endpoint.LanguageName, statement.Text);

        return error.Position is { } position ? error with { Position = statement.Start + position } : error;
    }

    /// <summary>A cursor request the server answered with an error: <see cref="CursorRejectedSentence"/> around the
    /// server's scrubbed reason, the server's error type kept as the code.</summary>
    private QueryError CursorRejected(string cursor, SearchResponse response)
    {
        var server = SearchErrors.ToQueryError(response.StatusCode, response.Body, language: null, statement: null);
        var reason = WithoutCursor(server.Message, cursor);

        return new QueryError(CursorRejectedSentence(Engine, response.StatusCode, reason), server.SqlState, null);
    }

    /// <summary>
    /// <paramref name="message"/> with the cursor cut out (§1.3: a cursor is never shown) — whole, and also where a
    /// bounded root cause (<see cref="SearchErrors.ToQueryError(int, byte[], string?, string?)"/>) was cut inside an
    /// echoed cursor, leaving a prefix of it before the "…".
    /// </summary>
    internal static string WithoutCursor(string message, string cursor)
    {
        var text = message.Replace(cursor, "[cursor]", StringComparison.Ordinal);
        for (var length = Math.Min(cursor.Length - 1, text.Length); length >= MinCursorFragment; length--)
        {
            var fragment = string.Concat(cursor.AsSpan(0, length), "…");
            if (text.Contains(fragment, StringComparison.Ordinal))
            {
                return text.Replace(fragment, "[cursor]…", StringComparison.Ordinal);
            }
        }

        return text;
    }

    /// <summary>The shortest cut-off cursor prefix <see cref="WithoutCursor"/> looks for.</summary>
    private const int MinCursorFragment = 8;

    // ---- requests, timeout, errors -----------------------------------------------------------------

    private Task<SearchResponse> SendCursorAsync(SearchContinuation state, CancellationToken ct)
        => _factory.SendAsync(
            HttpMethod.Post,
            state.Endpoint.Path,
            SearchProtocol.ContinuationBody(state.Endpoint, state.Cursor),
            SearchConnectionFactory.DefaultResponseLimitBytes,
            ct);

    /// <summary>One request on the streaming path, where failures are thrown: the connection's timeout comes out
    /// as a <see cref="TimeoutException"/> naming it, the caller's cancellation as itself, anything else as it
    /// came (the factory's exceptions are already scrubbed).</summary>
    private async Task<SearchResponse> StreamSendAsync(
        SearchEndpoint endpoint, byte[] body, CancellationTokenSource deadline, CancellationToken ct)
    {
        try
        {
            return await _factory
                .SendAsync(HttpMethod.Post, endpoint.Path, body, SearchConnectionFactory.DefaultResponseLimitBytes, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && TimedOut(deadline))
        {
            throw new TimeoutException(SearchErrors.TimeoutSentence(Engine, _factory.StatementTimeoutSeconds), e);
        }
    }

    /// <summary>The caller's token, plus the connection's statement timeout when it has one (§1.9a: a client-side
    /// cancel of the request).</summary>
    private CancellationTokenSource Deadline(CancellationToken ct)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_factory.StatementTimeoutSeconds > SessionPolicy.NoTimeout)
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(_factory.StatementTimeoutSeconds));
        }

        return deadline;
    }

    /// <summary>Our own clock fired. Only asked where the caller's token has not: a linked source is also
    /// cancelled by the caller.</summary>
    private bool TimedOut(CancellationTokenSource deadline)
        => deadline.IsCancellationRequested && _factory.StatementTimeoutSeconds > SessionPolicy.NoTimeout;

    /// <summary>A thrown failure as the grid's error. The timeout is ours to know — our own clock fired and the
    /// caller's did not — not something the exception can say.</summary>
    private QueryError ErrorFor(Exception e, CancellationTokenSource deadline)
        => SearchErrors.ToQueryError(e, Engine, _factory.StatementTimeoutSeconds, timedOut: TimedOut(deadline));

    private static QueryResult Failure(TimeSpan elapsed, QueryError error) => new(
        [], [], RowCount: 0, elapsed, Message: null, Error: error, Truncated: false);
}
