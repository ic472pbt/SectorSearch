﻿module IStack
    open System.Collections.Generic
    [<AbstractClass>]
    type IStack(blocks, needles: IList<string>) =
        abstract member Scan: byte [] * int64 -> byte []
        abstract member SearchIn: string -> int
