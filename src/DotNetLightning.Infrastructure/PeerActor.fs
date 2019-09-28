namespace DotNetLightning.Infrastructure


open System.IO.Pipelines
open System.Collections.Concurrent
open System.Threading.Tasks

open FSharp.Control.Tasks

open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

open NBitcoin
open DotNetLightning.Utils
open DotNetLightning.Chain
open DotNetLightning.Serialize.Msgs
open DotNetLightning.LN

open CustomEventAggregator
open DotNetLightning.Utils.Aether
open FSharp.Control.Reactive


type PeerError =
    | DuplicateConnection of PeerId
    | UnexpectedByteLength of expected: int * actual: int
    | EncryptorError of string

type IPeerActor =
    inherit IActor<PeerCommand>
    abstract member AcceptCommand : PeerCommandWithContext -> ValueTask

type PeerActor(keyRepo: IKeysRepository,
               log: ILogger<PeerActor>,
               nodeParams: IOptions<NodeParams>,
               eventAggregator: IEventAggregator,
               chainWatcher: IChainWatcher,
               broadCaster: IBroadCaster) as this =
    let _nodeParams = nodeParams.Value
    let ascii = System.Text.ASCIIEncoding.ASCII
    
    let _ourNodeId = keyRepo.GetNodeSecret().PubKey |> NodeId
    
    let mutable disposed = false
    
    member val KnownPeers = ConcurrentDictionary<PeerId, Peer>() with get, set
    
    member val OurNodeId = _ourNodeId with get
    
    member val OpenedPeers = ConcurrentDictionary<PeerId, Peer>() with get, set
    member val PeerIdToTransport = ConcurrentDictionary<PeerId, PipeWriter>() with get

    member val NodeIdToPeerId = ConcurrentDictionary<NodeId, PeerId>() with get, set
    member val CommunicationChannel = System.Threading.Channels.Channel.CreateBounded<ChannelEvent>(600) with get, set
            
    member private this.RememberPeersTransport(peerId: PeerId, pipe: PipeWriter) =
        match this.PeerIdToTransport.TryAdd(peerId, pipe) with
        | true -> ()
        | false ->
            sprintf "failed to remember peerId(%A) 's Transport. This should never happen" peerId
            |> log.LogCritical
            ()

    /// Initiate Handshake with peer by sending noise act-one
    /// `ie` is required only for testing. BOLT specifies specific ephemeral key for handshake
    member this.NewOutBoundConnection (theirNodeId: NodeId,
                                       peerId: PeerId,
                                       pipeWriter: PipeWriter,
                                       ?ie: Key) = vtask {
        let act1, peerEncryptor =
            PeerChannelEncryptor.newOutBound(theirNodeId)
            |> fun pce -> if (ie.IsNone) then pce else (Optic.set PeerChannelEncryptor.OutBoundIE_ ie.Value pce)
            |> PeerChannelEncryptor.getActOne
        sprintf "Going to create outbound peer for %A" peerId
        |> log.LogTrace
        let newPeer = {
                            ChannelEncryptor = peerEncryptor
                            IsOutBound = true
                            TheirNodeId = None
                            TheirGlobalFeatures = None
                            TheirLocalFeatures = None
                            SyncStatus = InitSyncTracker.NoSyncRequested
                            PeerId = peerId
                            GetOurNodeSecret = keyRepo.GetNodeSecret
                      }
        match this.KnownPeers.TryAdd(peerId, newPeer) with
        | false ->
            return peerId
                   |> DuplicateConnection
                   |> Result.Error
        | true ->
            this.RememberPeersTransport(peerId, pipeWriter)
            // send act1
            let! _ = pipeWriter.WriteAsync(act1)
            let! _ = pipeWriter.FlushAsync()
            return Ok()
        }

    member this.NewInboundConnection(theirPeerId: PeerId, actOne: byte[], pipeWriter: PipeWriter, ?ourEphemeral) = vtask {
        if (actOne.Length <> 50) then return (UnexpectedByteLength(50, actOne.Length) |> Result.Error) else
        let secret = keyRepo.GetNodeSecret()
        let peerEncryptor = PeerChannelEncryptor.newInBound(secret)
        let r =
            if (ourEphemeral.IsSome) then
                (PeerChannelEncryptor.processActOneWithEphemeralKey actOne secret ourEphemeral.Value peerEncryptor)
            else
                (PeerChannelEncryptor.processActOneWithKey actOne secret peerEncryptor)
        match r with
        | Bad b -> return (b.Describe() |> PeerError.EncryptorError |> Result.Error)
        | Good (actTwo, pce) ->
            let newPeer = {
                ChannelEncryptor = pce
                IsOutBound = false
                TheirNodeId = None
                TheirGlobalFeatures = None
                TheirLocalFeatures = None

                SyncStatus = InitSyncTracker.NoSyncRequested
                PeerId = theirPeerId
                GetOurNodeSecret = keyRepo.GetNodeSecret
            }
            match this.KnownPeers.TryAdd(theirPeerId, newPeer) with
            | false ->
                sprintf "duplicate connection with peer %A" theirPeerId
                |> log.LogInformation
                return theirPeerId
                |> DuplicateConnection
                |> Result.Error
            | true ->
                this.RememberPeersTransport(theirPeerId, pipeWriter)
                let! _ = pipeWriter.WriteAsync(actTwo)
                let! _ = pipeWriter.FlushAsync()
                return Ok ()
        }

    member private this.UpdatePeerWith(theirPeerId, newPeer: Peer) =
        this.KnownPeers.AddOrUpdate(theirPeerId, newPeer, (fun pId (oldPeer: Peer) -> newPeer))
        |> ignore
        if newPeer.ChannelEncryptor.IsReadyForEncryption() then
            this.OpenedPeers.AddOrUpdate(theirPeerId, newPeer, (fun pId (oldPeer: Peer) -> newPeer))
            |> ignore
    member private this.SetFeaturesToPeer(theirPeerId, gf: GlobalFeatures, lf: LocalFeatures) =
        let dummy = fun _ ->
            let msg = sprintf "Unknown peer. id (%A)" theirPeerId
            log.LogError(msg); failwith msg
        this.OpenedPeers.AddOrUpdate(theirPeerId, dummy, fun pId (oldPeer: Peer) -> { oldPeer with
                                                                                          TheirGlobalFeatures = Some gf
                                                                                          TheirLocalFeatures = Some lf })
    member private this.EncodeAndSendMsg(theirPeerId: PeerId, pipeWriter: PipeWriter) (msg: ILightningMsg) =
        log.LogTrace(sprintf "encoding and sending msg %A to peer %A" (msg.GetType()) theirPeerId)
        unitVtask {
                match this.OpenedPeers.TryGetValue(theirPeerId) with
                | true, peer ->
                    let msgEncrypted, newPCE =
                        peer.ChannelEncryptor |> PeerChannelEncryptor.encryptMessage (log.LogTrace) (msg.ToBytes())
                    this.UpdatePeerWith(theirPeerId, { peer with ChannelEncryptor = newPCE }) |> ignore
                    do! pipeWriter.WriteAsync(msgEncrypted)
                    return ()
                | false, _ ->
                    sprintf "peerId %A is not in opened peers" theirPeerId
                    |> log.LogCritical
        }

    /// wrapper to work on PeerChannelEncryptor in atomic way.
    /// i.e. retrieve old peer, operate on with it, and set updated peer to dict again.
    member private this.AtomicEncryptorOperation(peerId, f: Peer -> RResult<('a * PeerChannelEncryptor)>) =
        match this.KnownPeers.TryGetValue peerId with
        | false, _ ->
            sprintf "Unknown Peer %A. Failed to operate by Encryptor" peerId
            |> RResult.rmsg
        | true, peer ->
            match f(peer) with
            | Good(result, newPCE) ->
                this.UpdatePeerWith(peerId, { peer with ChannelEncryptor = newPCE })
                Good result
            | Bad e -> Bad e
    /// Atomic version of PeerChannelEncryptor.processActOneWithKey
    member private this.ProcessActOneWithKey(peerId, actOne: byte[], nodeSecret: Key) =
        log.LogTrace("processing act one")
        let innerOp peer = peer.ChannelEncryptor |> PeerChannelEncryptor.processActOneWithKey actOne nodeSecret
        this.AtomicEncryptorOperation(peerId, innerOp)

    /// Atomic version of PeerChannelEncryptor.processActTwo
    member private this.ProcessActTwo(peerId, actTwo, nodeSecret: Key) =
        log.LogTrace("processing act two")
        let innerOp = fun p -> p.ChannelEncryptor |> PeerChannelEncryptor.processActTwo actTwo nodeSecret
        this.AtomicEncryptorOperation(peerId, innerOp)
        
    /// Atomic version of PeerChannelEncryptor.processActThree
    member private this.ProcessActThree(peerId, actThree) =
        log.LogTrace("processing act three")
        let innerOp peer = peer.ChannelEncryptor |> PeerChannelEncryptor.processActThree actThree
        this.AtomicEncryptorOperation(peerId, innerOp)
    member private this.DecryptLengthHeader(peerId, headerCypherText) =
        let innerOp peer = peer.ChannelEncryptor |> PeerChannelEncryptor.decryptLengthHeader (log.LogTrace) headerCypherText
        this.AtomicEncryptorOperation(peerId, innerOp)
        
    member private this.DecryptMessage(peerId, cypherText) =
        let innerOp peer = peer.ChannelEncryptor |> PeerChannelEncryptor.decryptMessage (log.LogTrace) cypherText
        this.AtomicEncryptorOperation(peerId, innerOp)
        
    member private this.SetTheirNodeIdToPeer(peerId: PeerId, theirNodeId: NodeId) =
        let dum = fun x -> failwithf "Unknown Peer Id %A" peerId
        this.KnownPeers.AddOrUpdate(peerId, dum, fun v (oldPeer: Peer) -> { oldPeer with TheirNodeId = Some theirNodeId })
    
    member private this.HandleSetupMsgAsync(peerId, msg: ISetupMsg, pipe: IDuplexPipe) =
        vtask {
            let ok, peer = this.OpenedPeers.TryGetValue(peerId)
            assert ok
            match msg with
            | :? Init as init ->
                if (init.GlobalFeatures.RequiresUnknownBits()) then
                    log.LogInformation("Peer global features required unknown version bits")
                    return RResult.rbad(RBad.Object({ PeerHandleError.NoConnectionPossible = true }))
                else if (init.LocalFeatures.RequiresUnknownBits()) then
                    log.LogInformation("Peer local features required unknown version bits")
                    return RResult.rbad(RBad.Object({ PeerHandleError.NoConnectionPossible = true }))
                else if (peer.TheirGlobalFeatures.IsSome) then
                    return RResult.rbad(RBad.Object({ PeerHandleError.NoConnectionPossible = false }))
                else
                    sprintf "Received peer Init message: data_loss_protect: %s, initial_routing_sync: %s , upfront_shutdown_script: %s, unknown local flags: %s, unknown global flags %s" 
                        (if init.LocalFeatures.SupportsDataLossProect() then "supported" else "not supported")
                        (if init.LocalFeatures.InitialRoutingSync() then "supported" else "not supported")
                        (if init.LocalFeatures.SupportsUpfrontShutdownScript() then "supported" else "not supported")
                        (if init.LocalFeatures.SupportsUnknownBits() then "present" else "not present")
                        (if init.GlobalFeatures.SupportsUnknownBits() then "present" else "not present")
                        |> log.LogInformation
                    let theirNodeId = if peer.TheirNodeId.IsSome then peer.TheirNodeId.Value else
                                        let msg = "peer node id is not set. This should never happen"
                                        log.LogError msg
                                        failwith msg
                    eventAggregator.Publish<PeerEventWithContext>({ PeerEvent = ReceivedInit (init); NodeId = theirNodeId })
                    this.SetFeaturesToPeer(peerId, init.GlobalFeatures, init.LocalFeatures) |> ignore
                    if (not peer.IsOutBound) then
                        let lf = LocalFeatures.Flags([||]).SetInitialRoutingSync()
                        do! this.EncodeAndSendMsg(peer.PeerId, pipe.Output) ({ Init.GlobalFeatures = GlobalFeatures.Flags([||]); Init.LocalFeatures = lf })
                        return Good ()
                    else
                        eventAggregator.Publish<PeerEventWithContext>({ PeerEvent = Connected; NodeId = theirNodeId })
                        return Good ()
            | :? ErrorMessage as e ->
                let isDataPrintable = e.Data |> Array.exists(fun b -> b < 32uy || b > 126uy) |> not
                do
                    if isDataPrintable then
                        sprintf "Got error message from %A:%A" (peer.TheirNodeId.Value) (ascii.GetString(e.Data))
                        |> log.LogDebug
                    else
                        sprintf "Got error message from %A with non-ASCII error message" (peer.TheirNodeId.Value)
                        |> log.LogDebug
                if (e.ChannelId = WhichChannel.All) then
                    return
                        { PeerHandleError.NoConnectionPossible = true }
                        |> box
                        |> RBad.Object
                        |> RResult.rbad
                else
                    eventAggregator.Publish<PeerEventWithContext>({ PeerEvent = ReceivedError(e); NodeId = peer.TheirNodeId.Value } )
                    return Good ()
            | :? Ping as ping ->
                eventAggregator.Publish<PeerEventWithContext>({ PeerEvent = ReceivedPing (ping); NodeId = peer.TheirNodeId.Value })
                sprintf "Received ping from %A" peer.TheirNodeId.Value
                |> log.LogDebug
                if (ping.PongLen < 65532us) then
                    let pong = { Pong.BytesLen = ping.PongLen }
                    do! this.EncodeAndSendMsg(peer.PeerId, pipe.Output) (pong)
                    return Good()
                else
                    return Good()
            | :? Pong as pong ->
                sprintf "Received pong from %A"  peer.TheirNodeId.Value |> log.LogDebug
                eventAggregator.Publish<PeerEventWithContext>({ PeerEvent = ReceivedPong (pong); NodeId = peer.TheirNodeId.Value })
                return Good ()
            | _ -> return failwithf "Unknown setup message %A This should never happen" msg
        }

    member inline private this.TryPotentialHandleError (peerId, transport: IDuplexPipe) (b: RBad) =
        unitTask {
            let handleObj (o: obj) = 
                unitVtask {
                    match o with
                    | :? HandleError as he ->
                        sprintf "Got Error when handling message"
                        |> log.LogTrace
                        match he.Action with
                        | Some(DisconnectPeer _) ->
                            sprintf "disconnecting peer because %A" he.Error
                            |> log.LogInformation
                        | Some(IgnoreError) ->
                            sprintf "ignoring the error because %A" he.Error
                            |> log.LogDebug
                        | Some(SendErrorMessage msg) ->
                            sprintf "sending error message because %A" he.Error
                            |> log.LogDebug
                            let! _ = this.EncodeAndSendMsg(peerId, transport.Output) msg
                            return ()
                        | None ->
                            sprintf "Got error when handling message, action not yet filled in %A" he.Error
                            |> log.LogDebug
                    | _ ->
                        log.LogCritical(sprintf "Unknown Error object %A" o)
                }
            let! _ =
                unitTask {
                    match b with
                    | RBad.Exception ex -> log.LogError(ex.StackTrace)
                    | RBad.Message msg -> log.LogError(msg) 
                    | RBad.DescribedObject (msg, obj) ->
                        log.LogError(msg)
                        do! handleObj obj
                    | RBad.Object obj ->
                        do! handleObj obj
                }
            return ()
        }

    member private this.InsertNodeId(theirNodeId: NodeId, theirPeerId) =
        match this.NodeIdToPeerId.TryAdd(theirNodeId, theirPeerId) with
        | false ->
            log.LogDebug(sprintf "Got second connection with %A , closing." theirNodeId)
            RResult.rbad(RBad.Object { HandleError.Action = Some IgnoreError ; Error = sprintf "We already have connection with %A. nodeid: is %A" theirPeerId theirNodeId })
        | true ->
            log.LogTrace(sprintf "Finished noise handshake for connection with %A" theirNodeId)
            match this.KnownPeers.TryGetValue(theirPeerId) with
            | true, peer ->
                this.UpdatePeerWith(theirPeerId, { peer with TheirNodeId = Some theirNodeId })
                Good ()
            | false, _ ->
                RResult.rmsg("Failed to get from known peers")
    member this.AcceptCommand(cmd: PeerCommandWithContext) = unitVtask {
        match cmd with
        | { PeerCommand = Connect nodeId; PeerId = peerId } ->
            let pw = this.PeerIdToTransport.TryGet(peerId)
            match! this.NewOutBoundConnection(nodeId, peerId, pw) with
            | Ok _ -> return ()
            | Result.Error e ->
                sprintf "Failed to create outbound connection to the peer (%A) (%A)" peerId e |> log.LogError
                return ()
        | { PeerCommand = SendPing (ping); PeerId = peerId } ->
            let pw = this.PeerIdToTransport.TryGet(peerId)
            do! this.EncodeAndSendMsg (peerId, pw) (ping)
            return ()
    }
    
    member this.MakeLocalParams(channelPubKeys, defaultFinalScriptPubKey: Script, isFunder: bool, fundingSatoshis: Money) =
        _nodeParams.MakeLocalParams(this.OurNodeId, channelPubKeys, defaultFinalScriptPubKey, isFunder, fundingSatoshis)
        
    member this.StartAsync() = unitTask {
        let mutable notFinished = true
        while notFinished && (not disposed) do
            let! cont = this.CommunicationChannel.Reader.WaitToReadAsync()
            notFinished <- cont
            if notFinished && (not disposed) then
                match (this.CommunicationChannel.Reader.TryRead()) with
                | true, cmd ->
                    ()
                | false, _->
                    ()
        return ()
    }
        
    interface IPeerActor with
        member this.AcceptCommand(cmd) = this.AcceptCommand(cmd)
        member this.StartAsync () = this.StartAsync ()
        member this.Dispose() =
            disposed <- true
            ()
