
public sealed class AnalysisResultApiDiagnostics(ITestOutputHelper output)
{
    [LiveEtabsFact]
    public Task ResultApiCallShapesAndTablesAreRecordedReadOnly() => StaRunner.Run(() =>
    {
        using var session = EtabsSession.Attach(NullLogger.Instance);
        var model = session.ModelForVerification;
        var results = model.Results;
        var guard = new ApiGuard(NullLogger.Instance, default);

        var status = session.ReadAnalysisStatus();
        var identity = session.ReadIdentity();
        output.WriteLine($"Model: {session.ReadIdentity().FilePath}");
        output.WriteLine($"Finished cases: {status.Cases.Items.Count(c => c.State == AnalysisCaseState.Finished)}/{status.Cases.Items.Count}");

        ProbeStoryDrifts(results, guard, emptyArrays: false);
        ProbeStoryDrifts(results, guard, emptyArrays: true);
        ProbeModal(results, guard, emptyArrays: false);
        ProbeModal(results, guard, emptyArrays: true);
        ProbeBaseReact(results, guard, emptyArrays: false);
        ProbeBaseReact(results, guard, emptyArrays: true);

        var extracted = session.ReadSelectedAnalysisResults();
        output.WriteLine($"Selected cases: {string.Join(", ", extracted.SelectedCases)}");
        output.WriteLine($"Selected combinations: {string.Join(", ", extracted.SelectedCombinations)}");
        foreach (var table in extracted.ResultTables)
            output.WriteLine($"Table {table.Key}: IsEmpty={table.IsEmpty}; rows={table.Rows}; fields={string.Join(",", table.Fields)}");
        foreach (var issue in extracted.Issues)
            output.WriteLine($"Issue {issue.Component}: operation={issue.Operation}; code={issue.ReturnCode}; message={issue.Message}");

        foreach (var row in extracted.StoryDrifts.Items.Take(2)) output.WriteLine(System.Text.Json.JsonSerializer.Serialize(row));
        foreach (var key in new[] { "Joint Drifts", "Joint Displacements", "Story Drifts" }) foreach (var row in session.ReadTable(key).Rows.Take(2)) output.WriteLine(key + ": " + System.Text.Json.JsonSerializer.Serialize(row));
        Assert.NotEmpty(status.Cases.Items);
        Assert.NotEmpty(extracted.SelectedCases);
        Assert.Empty(extracted.Issues);
        Assert.NotEmpty(extracted.StoryDrifts.Items);
        Assert.NotEmpty(extracted.BaseReactions.Items);
        var jointTable = session.ReadTable("Joint Displacements");
        foreach (var drift in extracted.StoryDrifts.Items.Where(r => r.Case == "Dead"))
        {
            Assert.NotNull(drift.Displacement);
            var joint = Assert.Single(jointTable.Rows, r => r["Story"] == drift.Story && r["Label"] == drift.Label && r["OutputCase"] == drift.Case);
            var field = drift.Direction == "X" ? "Ux" : "Uy";
            var printed = joint[field];
            var number = double.Parse(printed, System.Globalization.CultureInfo.InvariantCulture);
            var parts = printed.ToLowerInvariant().Split('e');
            var decimals = parts[0].Contains('.') ? parts[0].Length - parts[0].IndexOf('.') - 1 : 0;
            var exponent = parts.Length == 2 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
            var tolerance = 0.5 * Math.Pow(10, exponent - decimals) + 1e-10 * Math.Max(1, Math.Abs(number));
            Assert.InRange(drift.Displacement.Value, number - tolerance, number + tolerance);
            double x = 0, y = 0, z = 0;
            guard.Call("Verification.PointObj.GetCoordCartesian", () => model.PointObj.GetCoordCartesian(joint["UniqueName"], ref x, ref y, ref z));
            Assert.Equal(x, drift.X); Assert.Equal(y, drift.Y); Assert.Equal(z, drift.Z);
        }
        Assert.True(ModelConsistency.SameContext(identity, session.ReadIdentity()));
        output.WriteLine($"Matched displacement rows: {extracted.StoryDrifts.Items.Count(r => r.Displacement.HasValue)}");
        return true;
    });

    private void ProbeStoryDrifts(cAnalysisResults results, ApiGuard guard, bool emptyArrays)
    {
        int count = 0;
        string[] story = emptyArrays ? [] : null!;
        string[] cases = emptyArrays ? [] : null!;
        string[] stepTypes = emptyArrays ? [] : null!;
        double[] steps = emptyArrays ? [] : null!;
        string[] directions = emptyArrays ? [] : null!;
        double[] drift = emptyArrays ? [] : null!;
        string[] labels = emptyArrays ? [] : null!;
        double[] x = emptyArrays ? [] : null!;
        double[] y = emptyArrays ? [] : null!;
        double[] z = emptyArrays ? [] : null!;
        var probe = Call(guard, "Diagnostics.Results.StoryDrifts", () => results.StoryDrifts(ref count,
            ref story, ref cases, ref stepTypes, ref steps, ref directions, ref drift, ref labels, ref x, ref y, ref z));
        Log("StoryDrifts", emptyArrays, probe, count, story, cases, stepTypes, steps, directions, drift, labels, x, y, z);
        if (probe.ReturnCode == 0)
            ApiGuard.Arrays("Diagnostics.Results.StoryDrifts", count, story, cases, stepTypes, steps,
                directions, drift, labels, x, y, z);
    }

    private void ProbeModal(cAnalysisResults results, ApiGuard guard, bool emptyArrays)
    {
        int count = 0;
        string[] cases = emptyArrays ? [] : null!;
        string[] stepTypes = emptyArrays ? [] : null!;
        double[] step = emptyArrays ? [] : null!;
        double[] period = emptyArrays ? [] : null!;
        double[] ux = emptyArrays ? [] : null!;
        double[] uy = emptyArrays ? [] : null!;
        double[] uz = emptyArrays ? [] : null!;
        double[] sumUx = emptyArrays ? [] : null!;
        double[] sumUy = emptyArrays ? [] : null!;
        double[] sumUz = emptyArrays ? [] : null!;
        double[] rx = emptyArrays ? [] : null!;
        double[] ry = emptyArrays ? [] : null!;
        double[] rz = emptyArrays ? [] : null!;
        double[] sumRx = emptyArrays ? [] : null!;
        double[] sumRy = emptyArrays ? [] : null!;
        double[] sumRz = emptyArrays ? [] : null!;
        var probe = Call(guard, "Diagnostics.Results.ModalParticipatingMassRatios", () =>
            results.ModalParticipatingMassRatios(ref count, ref cases, ref stepTypes, ref step, ref period,
                ref ux, ref uy, ref uz, ref sumUx, ref sumUy, ref sumUz, ref rx, ref ry, ref rz,
                ref sumRx, ref sumRy, ref sumRz));
        Log("ModalParticipatingMassRatios", emptyArrays, probe, count, cases, stepTypes, step, period,
            ux, uy, uz, sumUx, sumUy, sumUz, rx, ry, rz, sumRx, sumRy, sumRz);
        if (probe.ReturnCode == 0)
            ApiGuard.Arrays("Diagnostics.Results.ModalParticipatingMassRatios", count, cases, stepTypes,
                step, period, ux, uy, uz, sumUx, sumUy, sumUz, rx, ry, rz, sumRx, sumRy, sumRz);
    }

    private void ProbeBaseReact(cAnalysisResults results, ApiGuard guard, bool emptyArrays)
    {
        int count = 0;
        string[] cases = emptyArrays ? [] : null!;
        string[] stepTypes = emptyArrays ? [] : null!;
        double[] step = emptyArrays ? [] : null!;
        double[] fx = emptyArrays ? [] : null!;
        double[] fy = emptyArrays ? [] : null!;
        double[] fz = emptyArrays ? [] : null!;
        double[] mx = emptyArrays ? [] : null!;
        double[] my = emptyArrays ? [] : null!;
        double[] mz = emptyArrays ? [] : null!;
        double gx = 0, gy = 0, gz = 0;
        var probe = Call(guard, "Diagnostics.Results.BaseReact", () => results.BaseReact(ref count,
            ref cases, ref stepTypes, ref step, ref fx, ref fy, ref fz, ref mx, ref my, ref mz,
            ref gx, ref gy, ref gz));
        Log("BaseReact", emptyArrays, probe, count, cases, stepTypes, step, fx, fy, fz, mx, my, mz);
        if (probe.ReturnCode == 0)
            ApiGuard.Arrays("Diagnostics.Results.BaseReact", count, cases, stepTypes, step, fx, fy, fz, mx, my, mz);
    }

    private static ProbeResult Call(ApiGuard guard, string operation, Func<int> call)
    {
        try
        {
            guard.Call(operation, call);
            return new(0, null);
        }
        catch (ApiException exception)
        {
            return new(exception.ReturnCode, exception.Message);
        }
    }

    private void Log(string method, bool emptyArrays, ProbeResult probe, int count, params Array?[] arrays)
    {
        output.WriteLine($"{method} initial={(emptyArrays ? "empty" : "null")}: code={probe.ReturnCode}; count={count}; lengths=[{string.Join(",", arrays.Select(a => a?.Length.ToString() ?? "null"))}]; error={probe.Error ?? "none"}");
        Assert.Equal(0, probe.ReturnCode);
        Assert.True(count > 0, $"{method} returned no rows on the representative model.");
    }

    private sealed record ProbeResult(int? ReturnCode, string? Error);
}


