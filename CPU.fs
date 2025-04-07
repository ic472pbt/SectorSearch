module CPU
    open IStack
    open System
    open System.Collections.Generic
    type CPUStack(blocks, needles: IList<string>, matchAll) =
        inherit IStack(blocks, needles)

        let inspectSector(sector: Memory<byte>) =
            let s = System.Text.Encoding.ASCII.GetString(sector.Span)
            if matchAll then
                needles |> Seq.forall s.Contains
            else
                needles |> Seq.exists s.Contains
                
        override _.Scan(buffer: byte [], position: int64) =
            let sectors = buffer.Length / 512
            /// store the result. Result is 1 if a needle is found, 0 otherwise
            [0..sectors - 1]
                |> List.map (fun i -> async{
                        let sector = new Memory<byte>(buffer, i * 512, 512)
                        if inspectSector(sector) then
                            return 1uy
                        else
                            return 0uy 
                    })
                |> Async.Parallel
