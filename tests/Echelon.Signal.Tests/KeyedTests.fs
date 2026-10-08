/// Answer keys, preference scoring, benchmarks and group scoring (WI-0044):
/// SCS-004, SCS-006, SCS-007, ACR-002/VER-006 group scoring.
module Echelon.Signal.Tests.KeyedTests

open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Keyed

[<Fact>]
let ``single-choice keys score correct, incorrect and blank independently`` () =
    let key = { Correct = Set [ "b" ]; PointsCorrect = 2.0; PointsIncorrect = -1.0; PointsBlank = 0.0 }
    Assert.Equal(2.0, (single key (Some "b")).Final)
    let wrong = single key (Some "a")
    Assert.Equal(-1.0, wrong.Final)
    Assert.Equal(1.0, wrong.Penalty)
    Assert.Equal<string list>([ "a" ], wrong.Penalized)
    Assert.Equal(0.0, (single key None).Final)

[<Fact>]
let ``multi-choice keys: exact, any, all, none`` () =
    let picked = Set [ "a"; "b" ]
    Assert.Equal(1.0, (multi (ExactSetMatch(Set [ "a"; "b" ], 1.0)) picked).Final)
    Assert.Equal(0.0, (multi (ExactSetMatch(Set [ "a" ], 1.0)) picked).Final)
    Assert.Equal(1.0, (multi (AnyCorrect(Set [ "b"; "z" ], 1.0)) picked).Final)
    Assert.Equal(1.0, (multi (AllRequired(Set [ "a" ], 1.0, IgnoreExtra)) picked).Final)
    Assert.Equal(0.0, (multi (AllRequired(Set [ "a" ], 1.0, ExtraLosesCredit)) picked).Final)
    Assert.Equal(0.0, (multi (NoneForbidden(Set [ "b" ], 1.0)) picked).Final)
    Assert.Equal(3.0, (multi (OptionWeighted(Map [ "a", 1.0; "b", 2.0 ])) picked).Final)
    Assert.Equal(2.0, (multi CountSelected picked).Final)

[<Fact>]
let ``partial credit penalizes wrong picks, so selecting everything does not pay`` () =
    let key =
        PartialCredit
            { Correct = Set [ "a"; "b" ]
              PerCorrect = 1.0
              PerIncorrect = 1.0
              Floor = Some 0.0
              Cap = Some 2.0 }

    Assert.Equal(2.0, (multi key (Set [ "a"; "b" ])).Final)
    let all = multi key (Set [ "a"; "b"; "c"; "d" ])
    Assert.Equal(0.0, all.Final) // 2 credit - 2 penalty
    let worse = multi key (Set [ "c"; "d"; "e" ])
    Assert.Equal(0.0, worse.Final)
    Assert.True(worse.Floored)
    Assert.Equal(3.0, worse.Penalty)
    Assert.Equal(Scoring.Score(50.0, 4, 0), percentCorrect [ true; false; true; false ])
    Assert.Equal(Scoring.NotScored(Scoring.Undefined "no items to mark"), percentCorrect [])

[<Fact>]
let ``ranking methods are deterministic by position and item count`` () =
    let order = [ "x"; "y"; "z" ]
    let r m item = rank m 3 order item
    Assert.Equal(Some 3.0, r RankPoints "x")
    Assert.Equal(Some 0.0, r BordaCount "z")
    Assert.Equal(Some 0.5, r InverseRank "y")
    Assert.Equal(Some 1.0, r (TopKRankCredit(2, false)) "y")
    Assert.Equal(Some 1.0, r (TopKRankCredit(2, true)) "y")
    Assert.Equal(Some 0.0, r (TopKRankCredit(2, true)) "z")
    Assert.Equal(Some 5.0, r (PositionWeighted [ 10.0; 5.0 ]) "y")
    Assert.Equal(None, r RankPoints "w")

[<Fact>]
let ``allocation defines zero totals explicitly`` () =
    let a = Map [ "p", 60.0; "q", 40.0 ]
    Assert.Equal(Scoring.Score(60.0, 2, 0), allocation (AllocationSharePercent "p") a)
    Assert.Equal(Scoring.Score(0.4, 2, 0), allocation (NormalizedAllocation "q") a)
    Assert.Equal(Scoring.Score(100.0, 2, 0), allocation (WeightedAllocation(Map [ "p", 1.0; "q", 1.0 ])) a)
    Assert.Equal(Scoring.Score(20.0, 2, 0), allocation (DistanceFromTargetAllocation(Map [ "p", 50.0; "q", 50.0 ])) a)
    Assert.Equal(Scoring.NotScored(Scoring.Undefined "nothing was allocated"), allocation (NormalizedAllocation "p") (Map [ "p", 0.0 ]))

[<Fact>]
let ``pairwise and best-worst counts`` () =
    let pairs = [ "a", "b"; "a", "c"; "c", "a" ]
    Assert.Equal(2.0, winCount pairs "a")
    Assert.Equal(1.0, winLossDifference pairs "a")
    Assert.Equal(4.0, weightedWinCount (Map [ "b", 3.0 ]) pairs "a")
    let bw = [ "a", "c"; "a", "b"; "b", "a" ]
    Assert.Equal(1.0, bestMinusWorst bw "a")
    Assert.Equal(Scoring.Score(1.0 / 3.0, 3, 0), normalizedBestMinusWorst (Map [ "a", 3 ]) bw "a")
    Assert.Equal(Scoring.NotScored(Scoring.Undefined "'z' never appeared"), normalizedBestMinusWorst Map.empty bw "z")

[<Fact>]
let ``benchmarks are versioned by content and never fabricate a norm`` () =
    let b: Benchmark.Benchmark = { Id = "teams-2026"; Version = "1"; Values = [ 10.0; 20.0; 20.0; 30.0 ] }
    let pr = Benchmark.percentileRank b 20.0 |> Result.toOption |> Option.get
    Assert.Equal(50.0, pr.Value) // one below, two ties counted half: (1 + 1) / 4
    Assert.Equal(Benchmark.hash b, pr.BenchmarkHash)
    Assert.Equal("1", pr.BenchmarkVersion)
    Assert.NotEqual<string>(Benchmark.hash b, Benchmark.hash { b with Values = [ 10.0; 20.0 ] })
    let z = Benchmark.zScore b 30.0 |> Result.toOption |> Option.get
    Assert.Equal(1.414, System.Math.Round(z.Value, 3))
    let t = Benchmark.tScore Benchmark.Conventional b 20.0 |> Result.toOption |> Option.get
    Assert.Equal(50.0, t.Value)
    Assert.Equal(Error Benchmark.EmptyBenchmark, Benchmark.zScore { b with Values = [] } 1.0 |> Result.map ignore)
    Assert.Equal(Error Benchmark.ZeroStandardDeviation, Benchmark.zScore { b with Values = [ 5.0; 5.0 ] } 1.0 |> Result.map ignore)

[<Fact>]
let ``group scores exclude unscored contributions and suppress small anonymous groups`` () =
    let c = [ Some Groups.Self, Some 80.0; Some Groups.Peer, Some 40.0; Some Groups.Peer, Some 60.0; Some Groups.Manager, None ]
    let score mode m = GroupScoring.score mode 3 1 m c
    Assert.Equal(GroupScoring.GroupScored(60.0, 3, 1), score Import.IdentifiedGroup GroupScoring.GroupMean)
    Assert.Equal(GroupScoring.GroupScored(40.0, 3, 1), score Import.IdentifiedGroup (GroupScoring.GroupWeakest ResultModel.HigherIsBetter))
    // Self 80 and the peer mean 50, equally weighted: 65.
    let roles = GroupScoring.RoleBalanced [ Groups.Self, 1.0; Groups.Peer, 1.0; Groups.Manager, 1.0 ]
    Assert.Equal(GroupScoring.GroupScored(65.0, 3, 1), score Import.IdentifiedGroup roles)
    Assert.Equal(GroupScoring.Suppressed(2, 3), GroupScoring.score Import.AnonymousGroup 3 1 GroupScoring.GroupMean (List.take 2 c))
