module Log
    open NLog
    let logConfig = new NLog.Config.LoggingConfiguration()
    
    let initLog () =
        // Targets where to log to: File and Console
        let logfile = new NLog.Targets.FileTarget("logfile") 
        let logErrorFile = new NLog.Targets.FileTarget("logerrorfile") 
        do
            logErrorFile.DeleteOldFileOnStartup <- true
            logErrorFile.FileName <- $"{config.output_dir}\\errors.txt"
            logErrorFile.Layout <- Layouts.SimpleLayout("${longdate}|${level:uppercase=true}|${logger}|${message}")
        do
            logfile.DeleteOldFileOnStartup <- true
            logfile.FileName <- $"{config.output_dir}\\log.txt"
            logfile.Layout <- Layouts.SimpleLayout("${longdate}|${level:uppercase=true}|${logger}|${message}")
        let logconsole = new NLog.Targets.ConsoleTarget("logconsole")
                    
        // Rules for mapping loggers to targets            
        logConfig.AddRule(LogLevel.Info, LogLevel.Info, logfile);
        logConfig.AddRule(LogLevel.Debug, LogLevel.Debug, logconsole)
        logConfig.AddRule(LogLevel.Error, LogLevel.Fatal, logErrorFile)
                    
        // Apply config           
        NLog.LogManager.Configuration <- logConfig

    type LogMessage =
            | Progress of string
            | DocsProcessed of int
            | ZipsProcessed of int
    let loggerAgent = MailboxProcessor<LogMessage>.Start(fun inbox ->
        let rec loop(docs, zips) = async{
            match! inbox.Receive() with
            | DocsProcessed d -> return! loop(docs+d, zips)
            | ZipsProcessed z -> return! loop(docs, zips+z)
            | Progress s ->
                System.Console.CursorLeft <- 0                
                printf "%s docs %i zips %i" s docs zips
                return! loop(docs, zips)
            }
        loop(0, 0)
       )