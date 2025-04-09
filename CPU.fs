module CPU
    open IStack
    open System
    open System.Collections.Generic
    type CPUStack(blocks, needles: IList<string>, matchAll) =
        inherit IStack(blocks, needles)

        let inspectSector(sector: ReadOnlyMemory<byte>) =
            let s = System.Text.Encoding.ASCII.GetString(sector.Span)
            let utf16 = System.Text.Encoding.Unicode.GetString(sector.Span)
            if matchAll then
                if (needles |> Seq.forall s.Contains) || (needles |> Seq.forall utf16.Contains) then needles.Count else 0
            else
                (needles |> Seq.filter s.Contains |> Seq.length) + (needles |> Seq.filter utf16.Contains |> Seq.length)
                
        override _.Scan(buffer: byte [], position: int64) =
            let sectors = buffer.Length / 512
            [0..sectors - 1]
                |> List.map (fun i -> async{
                        let sector = ReadOnlyMemory<byte>(buffer, i * 512, 512)
                        return inspectSector(sector) |> byte
                    })
                |> Async.Parallel
