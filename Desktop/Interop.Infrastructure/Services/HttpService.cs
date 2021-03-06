﻿using Interop.Infrastructure.Events;
using Interop.Infrastructure.Interfaces;
using Interop.Infrastructure.Models;
using Interop.Modules.Client.Requests;

using Newtonsoft.Json;
using Prism.Events;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Interop.Infrastructure.Services
{
    public class HttpService : IHttpService
    {
        string REFRESH_RATE = "100";

        private static readonly object[] REQUESTS = { new GetTargets(), new GetObstacles(), new GetMissions() };
        private readonly ConcurrentDictionary<int, byte[]> _listOfImages = new ConcurrentDictionary<int, byte[]>();

        private readonly IEventAggregator _eventAggregator;
        CookieContainer _cookieContainer;

        public HttpService(IEventAggregator eventAggregator)
        {
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));

            _eventAggregator.GetEvent<UpdateTelemetry>().Subscribe(TryPostDroneTelemetry, true);
            _eventAggregator.GetEvent<PostTargetEvent>().Subscribe(TryPostTarget, true);
            _eventAggregator.GetEvent<PutTargetEvent>().Subscribe(TryUpdateTarget, true);
            _eventAggregator.GetEvent<DeleteTargetEvent>().Subscribe(TryDeleteTargetAsync, true);
        }

        public Uri HostAddress { get; set; }

        public async Task Run(CancellationToken cancellationToken)
        {
            try
            {
                var missions = await RunAsync<List<Mission>>((IRequest)REQUESTS[2]);
                _eventAggregator.GetEvent<UpdateMissionEvent>().Publish(missions);

                while (true)
                {
                    var targetTask = Task.Run(async () =>
                    {
                        var updatedTargets = await RunAsync<List<Target>>((IRequest)REQUESTS[0]);
                        _eventAggregator.GetEvent<UpdateTargetsEvent>().Publish(updatedTargets);

                        await LoadImages(updatedTargets);
                    }, cancellationToken);

                    var obstacleTask = Task.Run(async () =>
                    {
                        var updatedObstacles = await RunAsync<Obstacles>((IRequest)REQUESTS[1]);
                        _eventAggregator.GetEvent<UpdateObstaclesEvent>().Publish(updatedObstacles);
                    }, cancellationToken);

                    Task.WaitAll(targetTask, obstacleTask);

                    // You can decrease the value to get faster refresh
                    Task.Delay(100, cancellationToken).Wait(cancellationToken);
                }
            }
            catch (AggregateException aggEx)
            {
                var inners = aggEx.Flatten();
                Console.WriteLine($"   {inners.InnerException}");
            }
        }

        private async Task<T> RunAsync<T>(IRequest request) where T : class
        {
            using (var handler = new HttpClientHandler { CookieContainer = _cookieContainer })
            {
                using (var client = new HttpClient(handler))
                {
                    client.BaseAddress = HostAddress;
                    client.DefaultRequestHeaders.Accept.Clear();
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    // HTTP GET
                    var response = await client.GetAsync(request.Endpoint);
                    if (response.IsSuccessStatusCode)
                    {
                        string output = await response.Content.ReadAsStringAsync();
                        request.Data = JsonConvert.DeserializeObject<T>(output);

                        return (T)request.Data;
                    }
                }
            }

            return null;
        }

        public async Task<bool> Login(string username, string password, string iPaddress, string port)
        {
            try
            {
                HostAddress = new Uri($@"http://{iPaddress}:{port}");

                _cookieContainer = new CookieContainer();

                using (var handler = new HttpClientHandler() { CookieContainer = _cookieContainer })
                {
                    using (var client = new HttpClient(handler) { BaseAddress = HostAddress })
                    {
                        var content = new FormUrlEncodedContent(new[]
                        {
                            new KeyValuePair<string, string>("username", username),
                            new KeyValuePair<string, string>("password", password),
                        });

                        _cookieContainer.Add(HostAddress, new Cookie("CookieName", "cookie_value"));
                        var result = await client.PostAsync("/api/login", content);
                        result.EnsureSuccessStatusCode();
                        return result.IsSuccessStatusCode;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return false;
            }
        }

        private Task ExceptionHandler(Exception ex)
        {
            Console.WriteLine(ex);
            return Task.FromResult(true);
        }

        public async void TryPostDroneTelemetry(DroneTelemetry droneTelemetry)
        {
            if (this._cookieContainer != null && droneTelemetry.GlobalPositionInt != null)
            {
                try
                {
                    await PostDroneTelemetryAsync(droneTelemetry);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }
        }

        public async void TryPostTarget(InteropTargetMessage interopTargetMessage)
        {
            ExceptionDispatchInfo capturedException = null;

            try
            {
                if (interopTargetMessage != null)
                {
                    await PostTargetAsync(interopTargetMessage);
                }
            }
            catch (Exception ex)
            {
                capturedException = ExceptionDispatchInfo.Capture(ex);
            }

            if (capturedException != null)
            {
                await ExceptionHandler(capturedException.SourceException);
            }
        }

        public async void TryUpdateTarget(InteropTargetMessage interopTargetMessage)
        {
            ExceptionDispatchInfo capturedException = null;

            try
            {
                if (interopTargetMessage != null)
                {
                    await PutTargetAsync(interopTargetMessage);
                }
            }
            catch (Exception ex)
            {
                capturedException = ExceptionDispatchInfo.Capture(ex);
            }

            if (capturedException != null)
            {
                await ExceptionHandler(capturedException.SourceException);
            }
        }

        public async void TryDeleteTargetAsync(int interopId)
        {
            ExceptionDispatchInfo capturedException = null;

            try
            {
                if (interopId > -1)
                {
                    var isTargetDeleted = await DeleteTargetAsync(interopId);
                    if (isTargetDeleted)
                    {
                        _listOfImages.TryRemove(interopId, out _);
                    }
                }
            }
            catch (Exception ex)
            {
                capturedException = ExceptionDispatchInfo.Capture(ex);
            }

            if (capturedException != null)
            {
                await ExceptionHandler(capturedException.SourceException);
            }
        }

        public async Task<byte[]> LoadImageAsync(int id)
        {
            var emptyImage = new byte[1];

            using (var handler = new HttpClientHandler() { CookieContainer = _cookieContainer })
            {
                using (HttpClient client = new HttpClient(handler))
                {
                    client.BaseAddress = HostAddress;
                    client.DefaultRequestHeaders.Accept.Clear();
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));

                    using (var response = await client.GetAsync($"/api/odlcs/{id}/image").ConfigureAwait(false))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            var inputStream = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                            return inputStream;
                        }
                    }
                }
            }

            return emptyImage;
        }

        public async Task<bool> LoadImages(List<Target> targets)
        {
            var isImagesLoaded = false;

            foreach (var target in targets)
            {
                if (!this._listOfImages.ContainsKey(target.id))
                {
                    var imageBytes = await LoadImageAsync(target.id);
                    _listOfImages.TryAdd(target.id, imageBytes);
                    isImagesLoaded = true;
                }
            }

            _eventAggregator.GetEvent<TargetImagesEvent>().Publish(_listOfImages);
            return isImagesLoaded;
        }

        public async Task<bool> PostDroneTelemetryAsync(DroneTelemetry droneTelemetry)
        {
            try
            {
                using (var handler = new HttpClientHandler { CookieContainer = _cookieContainer })
                {
                    using (var client = new HttpClient(handler))
                    {
                        client.BaseAddress = HostAddress;

                        var content = new FormUrlEncodedContent(new[]
                        {
                            new KeyValuePair<string, string>("latitude", droneTelemetry.Latitude.ToString()),
                            new KeyValuePair<string, string>("longitude", droneTelemetry.Longitude.ToString()),
                            new KeyValuePair<string, string>("altitude_msl", droneTelemetry.AltitudeMSL.ToString()),
                            new KeyValuePair<string, string>("uas_heading", droneTelemetry.Heading.ToString()),
                        });

                        var result = await client.PostAsync("/api/telemetry", content).ConfigureAwait(false);
                        result.EnsureSuccessStatusCode();
                        return result.IsSuccessStatusCode;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public async Task<bool> PostTargetAsync(InteropTargetMessage interopMessage)
        {
            using (var handler = new HttpClientHandler { CookieContainer = _cookieContainer })
            {
                using (var client = new HttpClient(handler))
                {
                    client.BaseAddress = HostAddress;
                    var newTarget = new Target
                    {
                        type = interopMessage.TargetType.ToString(),
                        latitude = interopMessage.Latitude,
                        longitude = interopMessage.Longitude,
                        orientation = interopMessage.Orientation.ToString(),
                        shape = interopMessage.Shape.ToString(),
                        background_color = interopMessage.BackgroundColor.ToString(),
                        alphanumeric_color = interopMessage.ForegroundColor.ToString(),
                        alphanumeric = interopMessage.Character,
                        description = interopMessage.Description
                    };

                    var result = await client.PostAsync("/api/odlcs", new StringContent(JsonConvert.SerializeObject(newTarget))).ConfigureAwait(false);
                    var serverResponse = result.Content.ReadAsStringAsync();

                    var createdTarget = JsonConvert.DeserializeObject<Target>(serverResponse.Result);
                    if (createdTarget != null)
                    {
                        _eventAggregator.GetEvent<SetTargetIdEvent>().Publish(createdTarget.id);
                        await PostImageAsync(interopMessage, createdTarget.id);
                    }

                    result.EnsureSuccessStatusCode();
                    return result.IsSuccessStatusCode;
                }
            }
        }

        public async Task<bool> PostImageAsync(InteropTargetMessage interopMessage, int targetId)
        {
            using (var handler = new HttpClientHandler { CookieContainer = _cookieContainer })
            {
                using (var client = new HttpClient(handler))
                {
                    client.BaseAddress = HostAddress;
                    var imageBytes = new ByteArrayContent(interopMessage.Image);
                    imageBytes.Headers.ContentType = MediaTypeHeaderValue.Parse("image/jpeg");

                    var result = await client.PostAsync($"/api/odlcs/{targetId}/image", imageBytes).ConfigureAwait(false);
                    result.EnsureSuccessStatusCode();
                    return result.IsSuccessStatusCode;
                }
            }
        }

        public async Task<bool> PutTargetAsync(InteropTargetMessage interopMessage)
        {
            using (var handler = new HttpClientHandler { CookieContainer = _cookieContainer })
            {
                using (var client = new HttpClient(handler))
                {
                    client.BaseAddress = HostAddress;

                    var newTarget = new Target
                    {
                        type = interopMessage.TargetType.ToString(),
                        latitude = interopMessage.Latitude,
                        longitude = interopMessage.Longitude,
                        orientation = interopMessage.Orientation.ToString(),
                        shape = interopMessage.Shape.ToString(),
                        background_color = interopMessage.BackgroundColor.ToString(),
                        alphanumeric_color = interopMessage.ForegroundColor.ToString(),
                        alphanumeric = interopMessage.Character,
                        description = interopMessage.Description
                    };

                    var result = await client.PutAsync($"/api/odlcs/{interopMessage.InteropID}", new StringContent(JsonConvert.SerializeObject(newTarget))).ConfigureAwait(false);
                    result.EnsureSuccessStatusCode();
                    return result.IsSuccessStatusCode;
                }
            }
        }

        public async Task<bool> DeleteTargetAsync(int interopId)
        {
            using (var handler = new HttpClientHandler { CookieContainer = _cookieContainer })
            {
                using (var client = new HttpClient(handler))
                {
                    client.BaseAddress = HostAddress;

                    var result = await client.DeleteAsync($"/api/odlcs/{interopId}").ConfigureAwait(false);
                    result.EnsureSuccessStatusCode();
                    return result.IsSuccessStatusCode;
                }
            }
        }
    }
}
