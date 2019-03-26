﻿using Dapper;
using DevChatter.DevStreams.Core.Model;
using DevChatter.DevStreams.Core.Settings;
using Essenbee.Alexa.Lib;
using Essenbee.Alexa.Lib.Response;
using NodaTime;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;

namespace DevChatter.DevStreams.Web.Alexa
{
    public static class Responses
    {
        public static async Task<AlexaResponse> GetNextStreamResponse(string userTimeZone, string channel, 
            Channel dbChannel, DatabaseSettings dbSettings)
        {
            AlexaResponse response = null;

            if (dbChannel != null && !string.IsNullOrWhiteSpace(dbChannel.Name))
            {
                var name = dbChannel.Name;
                var id = dbChannel.Id;

                // ToDo: Extract this query
                var sessions = new List<StreamSession>();
                var now = $"{DateTime.UtcNow.Year}-{DateTime.UtcNow.Month}-{DateTime.UtcNow.Day} " +
                    $"{DateTime.UtcNow.Hour}:{DateTime.UtcNow.Minute}:{DateTime.UtcNow.Second}";

                string query = $"SELECT * FROM StreamSessions WHERE ChannelId = @id AND UtcStartTime > @now ORDER BY UtcStartTime";
                using (System.Data.IDbConnection connection = new SqlConnection(dbSettings.DefaultConnection))
                {
                    using (var multi = await connection.QueryMultipleAsync(query, new { id, now }))
                    {
                        sessions = (await multi.ReadAsync<StreamSession>()).ToList();
                    }
                }

                var nextStream = new StreamSession();
                var zonedDateTime = DateTime.MinValue;
                var nextStreamTimeFormatted = "currently has no future streams set up in the Dev Streams database";

                if (sessions.Count > 0)
                {
                    nextStream = sessions.FirstOrDefault();

                    if (nextStream != null)
                    {
                        DateTimeZone zone = DateTimeZoneProviders.Tzdb[userTimeZone];
                        zonedDateTime = nextStream.UtcStartTime.InZone(zone).ToDateTimeUnspecified();
                        nextStreamTimeFormatted = string.Format("will be streaming next on {0:dddd, MMMM dd} at {0:h:mm tt}", zonedDateTime, zonedDateTime);
                    }
                }

                response = new ResponseBuilder()
                    .Say($"{name} {nextStreamTimeFormatted}")
                    .Build();
            }
            else
            {
                response = new ResponseBuilder()
                    .Say($"Sorry, I cound not find {channel} in my database of live coding streamers")
                    .Build();
            }

            return response;
        }

        public static AlexaResponse GetLiveNowResponse(List<string> liveChannels)
        {
            var response = new ResponseBuilder()
                .Say("None of the streamers in my database are currently broadcasting")
                .WriteSimpleCard("Streaming Now!", "None")
                .Build();

            if (liveChannels != null && liveChannels.Count > 0)
            {
                var firstFew = string.Join(", ", liveChannels.Take(3));

                var text1 = liveChannels.Count == 1
                    ? $"{liveChannels.First()} is broadcasting now."
                    : $"{liveChannels.Count} streamers are broadcasting now:";

                var text2 = string.Empty;

                if (liveChannels.Count == 2)
                {
                    text2 = $"{liveChannels[0]} and {liveChannels[1]}";
                }

                if (liveChannels.Count == 3)
                {
                    text2 = $"{liveChannels[0]}, {liveChannels[1]} and {liveChannels[2]}";
                }

                if (liveChannels.Count > 3)
                {
                    text2 = $"Here are the first three: {firstFew}";
                }

                response = new ResponseBuilder()
                    .Say($"{text1} {text2}")
                    .WriteSimpleCard("Streaming Now!", $"{firstFew}")
                    .Build();
            }

            return response;
        }
    }
}
