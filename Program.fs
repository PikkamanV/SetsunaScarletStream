open System
open System.IO
open System.Diagnostics
open System.Net.Http
open FSharp.Data
open Akka.FSharp

// HttpClient singleton
module HttpClientSingleton =
    let client = new HttpClient()

// Configuration type
type Config = JsonProvider<"""{
    "shows": [
        {
            "name": "Show1",
            "url": "https://stream.example.com/stream.m3u8",
            "schedule": [
                { "dayOfWeek": "Monday", "startTime": "20:00", "endTime": "21:00" },
                { "dayOfWeek": "Wednesday", "startTime": "21:00", "endTime": "22:00" }
            ]
        }
    ],
    "outputDirectory": "/app/recordings",
    "slackWebhookUrl": "https://hooks.slack.com/services/xxx/yyy/zzz"
}""">

// Load configuration
let config = Config.Load("config.json")

// Show type
type Show = {
    Name: string
    Url: string
    Schedule: (DayOfWeek * TimeSpan * TimeSpan) list  // (day, startTime, endTime)
}

// Convert configuration to Show list
let shows = 
    config.Shows 
    |> Array.map (fun s -> 
        { 
            Name = s.Name
            Url = s.Url
            Schedule = 
                s.Schedule 
                |> Array.map (fun sch -> 
                    (Enum.Parse(typeof<DayOfWeek>, sch.DayOfWeek) :?> DayOfWeek, 
                     sch.StartTime,
                     sch.EndTime))
                |> Array.toList
        })
    |> Array.toList

// Actor messages
type RecorderMessage =
    | StartRecording of Show * DateTime
    | StopRecording of Show * DateTime
    | CheckSchedule

// Logger
let log message =
    let logMessage = sprintf "[%s] %s" (DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")) message
    File.AppendAllText("recorder.log", logMessage + Environment.NewLine)
    printfn "%s" logMessage

// Slack notification
let sendSlackNotification message =
    async {
        let content = new StringContent(sprintf """{"text":"%s"}""" message, System.Text.Encoding.UTF8, "application/json")
        try
            let! response = HttpClientSingleton.client.PostAsync(config.SlackWebhookUrl, content) |> Async.AwaitTask
            response.EnsureSuccessStatusCode() |> ignore
            log $"Slack notification sent: {message}"
        with
        | ex -> log $"Error sending Slack notification: {ex.Message}"
    } |> Async.Start

// Recording function
let recordShow (show: Show) (startTime: DateTime) =
    let currentSchedule = 
        show.Schedule 
        |> List.tryFind (fun (day, start, _) -> 
            day = startTime.DayOfWeek && start <= startTime.TimeOfDay && startTime.TimeOfDay < start.Add(TimeSpan.FromMinutes(1.0)))
    
    match currentSchedule with
    | Some (_, startTimeSpan, endTimeSpan) ->
        let startTime = startTime.Date + startTimeSpan
        let endTime = startTime.Date + endTimeSpan
        let duration = endTime - startTime

        let outputFileName = sprintf "%s_%s.mp4" show.Name (startTime.ToString("yyyyMMddHHmmss"))
        let outputPath = Path.Combine(config.OutputDirectory, show.Name, outputFileName)

        // Ensure directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)) |> ignore

        let startRecording() =
            use proc = new Process()
            proc.StartInfo.FileName <- "ffmpeg"
            proc.StartInfo.Arguments <- sprintf "-i %s -c copy -t %d %s" show.Url (int duration.TotalSeconds) outputPath
            proc.StartInfo.UseShellExecute <- false
            proc.StartInfo.RedirectStandardError <- true
            proc.EnableRaisingEvents <- true
            
            let mutable error = ""
            proc.ErrorDataReceived.Add(fun e -> if not (isNull e.Data) then error <- error + e.Data + Environment.NewLine)
            
            proc.Start() |> ignore
            proc.BeginErrorReadLine()
            log $"Started recording: {show.Name}"

            if proc.WaitForExit((int)duration.TotalMilliseconds + 3000) then // Wait for duration plus 3 seconds
                if proc.ExitCode = 0 then
                    log $"Finished recording: {show.Name}"
                    sendSlackNotification $"Recording completed: {show.Name}"
                    true
                else
                    log $"Error during recording: {show.Name}. Exit code: {proc.ExitCode}. Error: {error}"
                    sendSlackNotification $"Recording failed: {show.Name}. Check logs for details."
                    false // Indicate failure
            else
                proc.Kill()
                log $"Recording timeout: {show.Name}"
                sendSlackNotification $"Recording timeout: {show.Name}"
                false // Indicate failure

        // Retry logic
        let rec retry attempts =
            if attempts > 0 then
                if not (startRecording()) then
                    log $"Retrying recording: {show.Name}. Attempts left: {attempts - 1}"
                    retry (attempts - 1)
            else
                log $"Failed to record after all retry attempts: {show.Name}"
                sendSlackNotification $"Recording failed after all retry attempts: {show.Name}"

        retry 3 // Try up to 3 times
    | None ->
        log $"Error: No matching schedule found for {show.Name} at {startTime}"
        sendSlackNotification $"Error: No matching schedule found for {show.Name} at {startTime}"

// Recorder actor
let recorderActor (mailbox: Actor<RecorderMessage>) =
    let rec loop() = actor {
        let! message = mailbox.Receive()
        match message with
        | StartRecording (show, startTime) ->
            recordShow show startTime
        | StopRecording (show, _) ->
            log $"Stopped recording: {show.Name}"
        | CheckSchedule ->
            let now = DateTime.Now
            shows 
            |> List.iter (fun show ->
                show.Schedule 
                |> List.iter (fun (day, startTime, _) ->
                    if day = now.DayOfWeek && 
                       startTime <= now.TimeOfDay && 
                       now.TimeOfDay < startTime.Add(TimeSpan.FromSeconds(5.0)) then
                        mailbox.Self <! StartRecording(show, now.Date + startTime)))
            
            // Schedule next check in 5 seconds
            mailbox.Context.System.Scheduler.ScheduleTellOnce(
                TimeSpan.FromSeconds(5.0), mailbox.Self, CheckSchedule)

        return! loop()
    }
    loop()

[<EntryPoint>]
let main argv =
    let system = System.create "RecorderSystem" (Configuration.load())
    let recorder = spawn system "recorder" recorderActor

    // Start the scheduling process
    recorder <! CheckSchedule

    // Keep the application running
    Console.WriteLine("Recorder is running. Press Enter to exit...")
    Console.ReadLine() |> ignore

    // Terminate the actor system
    system.Terminate() |> ignore
    system.WhenTerminated.Wait()

    // Dispose HttpClient
    HttpClientSingleton.client.Dispose()

    0 // return an integer exit code