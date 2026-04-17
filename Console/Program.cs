// See https://aka.ms/new-console-template for more information

using RouteAnalyzer.Core.GtfsParser;
using RouteAnalyzer.Core.Model.Connection;
using RouteAnalyzer.Core.Model.FootPath;

Console.WriteLine("Hello, World!");

var feed = GtfsLoader.LoadGtfsFeed(@"Test\TestData");

var connections = ConnectionBuilder.Build(feed.StopTimes);

var foodPaths = FootPathBuilder.Build(feed.Stops);

