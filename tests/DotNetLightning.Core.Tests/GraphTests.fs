module GraphTests


open DotNetLightning.Utils
open DotNetLightning.Routing
open DotNetLightning.Routing.Graph
open DotNetLightning.Serialize.Msgs
open NBitcoin
open Expecto
open NBitcoin.DataEncoders
open DotNetLightning.FGL
open DotNetLightning.FGL.Directed

let hex = Encoders.Hex

module Constants =
    let ascii = System.Text.ASCIIEncoding.ASCII
    let signMessageWith (privKey: Key) (msgHash: string) =
        let msgBytes = msgHash |> ascii.GetBytes
        privKey.SignCompact(msgBytes |> uint256, false) |> fun d -> LNECDSASignature.FromBytesCompact(d, true)
    let DEFAULT_AMOUNT_MSAT = LNMoney.MilliSatoshis(10000000L)
    let DEFAULT_ROUTE_PARAMS = { RouteParams.Randomize = false
                                 MaxFeeBase = LNMoney.MilliSatoshis(21000L)
                                 MaxFeePCT = 0.03
                                 RouteMaxCLTV = 2016
                                 RouteMaxLength = 6
                                 Ratios = None }
    let privKey1 = Key(hex.DecodeData("0101010101010101010101010101010101010101010101010101010101010101"))
    
    let DUMMY_SIG = signMessageWith privKey1 "01010101010101010101010101010101"
    
/// Taken from eclair-core
let pks =
    [
        "02999fa724ec3c244e4da52b4a91ad421dc96c9a810587849cd4b2469313519c73"; //a
        "03f1cb1af20fe9ccda3ea128e27d7c39ee27375c8480f11a87c17197e97541ca6a"; //b
        "0358e32d245ff5f5a3eb14c78c6f69c67cea7846bdf9aeeb7199e8f6fbb0306484"; //c
        "029e059b6780f155f38e83601969919aae631ddf6faed58fe860c72225eb327d7c"; //d
        "02f38f4e37142cc05df44683a83e22dea608cf4691492829ff4cf99888c5ec2d3a"; //e
        "03fc5b91ce2d857f146fd9b986363374ffe04dc143d8bcd6d7664c8873c463cdfc"; //f
        "03864ef025fde8fb587d989186ce6a4a186895ee44a926bfc370e2c366597a3f8f"; //g
    ]
    |> List.map (hex.DecodeData >> PubKey >> NodeId)
let a, b, c, d, e, f, g = pks.[0], pks.[1], pks.[2], pks.[3], pks.[4], pks.[5], pks.[6]

/// TODO: use maxHtlc properly
let makeUpdate (shortChannelId: ShortChannelId,
                nodeid1: NodeId,
                nodeid2: NodeId,
                feeBase: LNMoney,
                feeProportionalMillions: uint32,
                minHtlc: LNMoney option,
                maxHtlc: LNMoney option,
                cltvDelta: BlockHeightOffset option
                ): (ChannelDesc * ChannelUpdate) =
    let minHtlc = Option.defaultValue Constants.DEFAULT_AMOUNT_MSAT minHtlc
    let cltvDelta = Option.defaultValue (BlockHeightOffset(0us)) cltvDelta
    let desc = { ChannelDesc.ShortChannelId = shortChannelId
                 A = nodeid1
                 B = nodeid2 }
    let update = { ChannelUpdate.Signature = Constants.DUMMY_SIG
                   Contents = { UnsignedChannelUpdate.MessageFlags =
                                    match maxHtlc with Some _ -> 1uy | _ -> 0uy
                                ChannelFlags = 0uy
                                ChainHash = Network.RegTest.GenesisHash
                                ShortChannelId = shortChannelId
                                Timestamp = 0u
                                CLTVExpiryDelta = cltvDelta
                                HTLCMinimumMSat = minHtlc
                                FeeBaseMSat = feeBase
                                FeeProportionalMillionths = feeProportionalMillions
                                HTLCMaximumMSat = None }
                 }
    desc, update
    
let makeUpdateSimple (shortChannelId, a, b) =
    makeUpdate(ShortChannelId.FromUInt64(shortChannelId), a, b, LNMoney.Zero, 0u, None, None, None)
let makeTestGraph() =
    let updates =
        [
            a, b, (makeUpdate(ShortChannelId.FromUInt64(1UL), a, b, LNMoney.Zero, 0u, None, None, None))
            b, c, (makeUpdate(ShortChannelId.FromUInt64(2UL), b, c, LNMoney.Zero, 0u, None, None, None))
            c, d, (makeUpdate(ShortChannelId.FromUInt64(3UL), c, d, LNMoney.Zero, 0u, None, None, None))
            d, e, (makeUpdate(ShortChannelId.FromUInt64(4UL), d, e, LNMoney.Zero, 0u, None, None, None))
            e, f, (makeUpdate(ShortChannelId.FromUInt64(5UL), e, f, LNMoney.Zero, 0u, None, None, None))
            f, g, (makeUpdate(ShortChannelId.FromUInt64(6UL), f, g, LNMoney.Zero, 0u, None, None, None))
        ]
    Graph.empty |> Edges.addMany updates
[<Tests>]
let graphTests =
    ftestList "GraphTests from eclair" [
        testCase "Instantiate a graph, with vertices and then add edges" <| fun _ ->
            let g =
                DirectedLNGraph.Create()
                    .AddVertex(a)
                    .AddVertex(b)
                    .AddVertex(c)
                    .AddVertex(d)
                    .AddVertex(e)
            Expect.isTrue(g.ContainsVertex(a) && g.ContainsVertex(e)) ""
            Expect.equal (g.VertexSet().Length) 5 ""
            let otherGraph = g.AddVertex(a)
            Expect.equal (otherGraph.VertexSet().Length) 5 ""
            let descAB, updateAB = makeUpdate(ShortChannelId.FromUInt64(1UL), a, b, LNMoney.Zero, 0u, None, None, None)
            let descBC, updateBC = makeUpdate(ShortChannelId.FromUInt64(2UL), b, c, LNMoney.Zero, 0u, None, None, None)
            let descAD, updateAD = makeUpdate(ShortChannelId.FromUInt64(3UL), a, d, LNMoney.Zero, 0u, None, None, None)
            let descDC, updateDC = makeUpdate(ShortChannelId.FromUInt64(4UL), d, c, LNMoney.Zero, 0u, None, None, None)
            let descCE, updateCE = makeUpdate(ShortChannelId.FromUInt64(5UL), c, e, LNMoney.Zero, 0u, None, None, None)
            let graphWithEdges =
                g
                    .AddEdge({ Update = updateAB; Desc = descAB })
                    .AddEdge({ Update = updateBC; Desc = descBC })
                    .AddEdge({ Update = updateAD; Desc = descAD })
                    .AddEdge({ Update = updateDC; Desc = descDC })
                    .AddEdge({ Update = updateCE; Desc = descCE })
            Expect.equal (graphWithEdges.OutgoingEdgesOf(a).Value.Length) 2 ""
            Expect.equal (graphWithEdges.OutgoingEdgesOf(b).Value.Length) 1 ""
            Expect.equal (graphWithEdges.OutgoingEdgesOf(c).Value.Length) 1 ""
            Expect.equal (graphWithEdges.OutgoingEdgesOf(d).Value.Length) 1 ""
            Expect.equal (graphWithEdges.OutgoingEdgesOf(e).Value.Length) 0 ""
            Expect.isNone (graphWithEdges.OutgoingEdgesOf(f)) ""
            
            let withRemovedEdges = graphWithEdges.RemoveEdge(descAD)
            Expect.equal (withRemovedEdges.OutgoingEdgesOf(d).Value.Length) 1 ""
            
        testCase "instantiate a graph adding edges only" <| fun _ ->
            let labelAB =
                makeUpdateSimple(1UL, a, b)
                |> fun (a, b) -> { Desc = a; Update = b }
            let labelBC = makeUpdateSimple(2UL, b, c) |> fun (a, b) -> { Desc = a; Update = b }
            let labelAD = makeUpdateSimple(3UL, a, d) |> fun (a, b) -> { Desc = a; Update = b }
            let labelDC = makeUpdateSimple(4UL, d, c) |> fun (a, b) -> { Desc = a; Update = b }
            let labelCE = makeUpdateSimple(5UL, c, e) |> fun (a, b) -> { Desc = a; Update = b }
            let labelBE = makeUpdateSimple(6UL, b, e) |> fun (a, b) -> { Desc = a; Update = b }
            let g =
                DirectedLNGraph.Create()
                    .AddEdge(labelAB)
                    .AddEdge(labelBC)
                    .AddEdge(labelAD)
                    .AddEdge(labelDC)
                    .AddEdge(labelCE)
                    .AddEdge(labelBE)
            Expect.equal (g.VertexSet().Length) 5 ""
            Expect.equal (g.OutgoingEdgesOf(c).Value.Length) 1 ""
            Expect.equal (g.IncomingEdgesOf(c).Value.Length) 2 ""
            
        testCase "containsEdge should return true if the graph contains that edge, false otherwise" <| fun _ ->
            let updates = seq { makeUpdateSimple(1UL, a, b) }
            ()
    ]
