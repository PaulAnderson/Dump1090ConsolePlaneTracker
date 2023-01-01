using System;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

internal class Program
{
    private static void Main(string[] args)
    {
        //Configuration to be completed:
        string IpAddress = "127.0.0.1"; // The IP address where Dump1090 is running. 
        const int TCPPort = 30003; // Default 30003

        const double myLat = -37.8102; //Reciever Latitude in decimal format
        const double myLon = 144.9628; //Reciever Longitude in decimal format
        const double myAltitudeInMeters = 41; //Receiver Altitude in Meters
 
        const bool logData = false; //Log Dump1090 data in a file

        const double UpdateFrequencySeconds = .5; //How often to redraw the chart and update displayed data
        const int MaximumAgeSeconds = 5; //How long to keep a plane in view after no data is received for that plane

        //Conversion constants
        const double FeetToMetersRatio = 0.3048;

        //Shared Variables
        var hexFlights = new Dictionary<string,string>();
        var planeData = new Dictionary<string,PlaneData>();
        var chartScale = 1.0d;

        //Connect to Dump1090
        IPAddress ipAddress = IPAddress.Parse(IpAddress);
        IPEndPoint endPoint = new IPEndPoint(ipAddress, TCPPort);
        Socket socket = new Socket(ipAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(endPoint);

        Console.WriteLine($"Connected to {IpAddress}:{TCPPort}");

        var displayRendered = DateTime.Now;
        // Read data from the socket
        while (true)
        {
            // Read a line of data from the socket
            byte[] buffer = new byte[1024];
            int bytesReceived = socket.Receive(buffer);
            string data = Encoding.ASCII.GetString(buffer, 0, bytesReceived);

            if (logData)
            {
                using (FileStream stream = new FileStream("dump1090.txt", FileMode.Append, FileAccess.Write))
                {
                    stream.Write(buffer, 0, bytesReceived);
                }
            }

            //TODO ensure whole lines are read from the buffer                     
            var lines = data.Split("\n\r".ToCharArray(),StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (string line in lines)
            {
                // Split the line into fields
                string[] fields = line.Split(',');
                if (fields.Length < 16) continue;
                if (fields[0]!="MSG") continue;

                // Parse the message type
                int type = int.Parse(fields[1]);
                string hex = fields[4];

                // Handle each message type separately
                switch (type)
                {
                    case 1:
                        //Parse message type 1 All-Call Reply message
                        string flight = fields[10]?.Trim() ?? "";

                        if (!string.IsNullOrEmpty(flight) && ! hexFlights.ContainsKey(hex)) {
                            hexFlights.Add(hex,flight);
                            //Console.WriteLine($"All-Call Reply received. Enrolled Flight {flight} as hex {hex}");
                        }
                        if (!string.IsNullOrEmpty(flight) && ! planeData.ContainsKey(hex)) {
                            planeData.Add(hex,new PlaneData() {HexCode = hex, FlightNumber = flight});
                            //Console.WriteLine($"All-Call Reply received. Enrolled Flight {flight} as hex {hex}");
                        }
                        break;

                    case 3:
                        
                        // Parse message type 3 Short Air-to-Air Surveillance (AAS) message
                        string dateTime1 = fields[5];
                        string dateTime2 = fields[6];
                        string altitude = fields[11];
                        try {
                            var latitude = double.Parse(fields[14]);
                            var longitude = double.Parse(fields[15]);
                            int altitudeFeet = int.Parse(altitude);
                            double altitudeMeters = altitudeFeet * FeetToMetersRatio;

                            string flightNo = "";
                            hexFlights.TryGetValue(hex,out flightNo);

                            double distanceKM = Distance(myLat,myLon,latitude,longitude);
                            double bearing = Heading(myLat,myLon,latitude,longitude);
                            double verticalAngle = VerticalAngle(myAltitudeInMeters,altitudeMeters,distanceKM);

                            if (planeData.TryGetValue(hex,out var plane)) {
                                plane.AltitudeFeet = altitudeFeet;
                                plane.BearingDegrees = bearing;
                                plane.DistanceKM=distanceKM;
                                plane.ElevationAngleDegrees = verticalAngle;
                                if (string.IsNullOrEmpty(plane.FlightNumber)) {
                                    plane.FlightNumber = flightNo;
                                }
                                plane.Latitude = latitude;
                                plane.Longitude = longitude;

                                plane.LastSeen = DateTime.Now;
                                plane.LocationUpdated = DateTime.Now;

                                //plane.speedKnots = 
                                //TODO calculate ground speed based on time and position
                            }

                            //Console.WriteLine($"Time: {dateTime1}, Distance {distanceKM:F2} KM, Bearing {bearing:F2}° , Elevation Angle: {verticalAngle:F2}° , Hex: {hex}, Flight {flightNo}, Latitude: {latitude}, Longitude: {longitude}, Altitude: {altitudeFeet} ft");
                        } catch (FormatException ex) {
                            //lazy error handling. Just skip row if any parse errors
                            continue;
                        }
                        break;
                    default:
                        break;
                }

            }

            //Get key presses
            if (Console.KeyAvailable)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.KeyChar == '+')
                {
                    chartScale /= 1.5;
                    if (chartScale <= .01) chartScale = .01;
                    displayRendered = DateTime.Now.AddSeconds(-UpdateFrequencySeconds);
                }
                else if (key.KeyChar == '-')
                {
                    chartScale *= 1.5;
                    if (chartScale >= 128) chartScale = 128;
                    displayRendered = DateTime.Now.AddSeconds(-UpdateFrequencySeconds);
                }
            }

            //Show all visible planes on a graph
            if (planeData.Count>0 && DateTime.Now.Subtract(displayRendered).TotalSeconds>UpdateFrequencySeconds)
            {
                var planes = planeData.Values.ToList<PlaneData>();
                char[,] chart = PopulateChart(planes, 100, 50, chartScale);
                RenderChart(chart);

                Console.WriteLine($"Chart Scale: 1 char = {chartScale:F2} KM   (Use +/- keys to change");

                foreach (var plane in planes)
                {
                    Console.WriteLine($"Time: {plane.LastSeen}, Distance {plane.DistanceKM:F2} KM, Bearing {plane.BearingDegrees:F2}° , Elevation Angle: {plane.ElevationAngleDegrees:F2}° , Hex: {plane.HexCode}, Flight {plane.FlightNumber}, Latitude: {plane.Latitude}, Longitude: {plane.Longitude}, Altitude: {plane.AltitudeFeet} ft, Speed: {plane.SpeedKnots} kts");
                    if (DateTime.Now.Subtract(plane.LastSeen).TotalSeconds > MaximumAgeSeconds)
                    {
                        planeData.Remove(plane.HexCode);
                    }
                }

                displayRendered = DateTime.Now;
            }
            
        }
    }

        static double Distance(double lat1, double lon1, double lat2, double lon2)
        {
            // Convert the latitude and longitude values to radians
            lat1 = ToRadians(lat1);
            lon1 = ToRadians(lon1);
            lat2 = ToRadians(lat2);
            lon2 = ToRadians(lon2);

            // Calculate the difference between the longitudes
            double dlon = lon2 - lon1;

            // Calculate the distance using the Haversine formula
            double a = Math.Pow(Math.Sin(dlon / 2), 2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Pow(Math.Sin((lat2 - lat1) / 2), 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            double d = 6371 * c; // 6371 is the radius of the Earth in kilometers

            return d;
        }

        static double Heading(double lat1, double lon1, double lat2, double lon2) {
             // Calculate the heading using the inverse tangent
            double dlon = lon2 - lon1;
            double y = Math.Sin(dlon) * Math.Cos(lat2);
            double x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dlon);
            var heading = ToDegrees(Math.Atan2(y, x));
            if (heading < 0)
            {
                heading += 360;
            }
            return heading;
        }

        static double VerticalAngle(double alt1_meters,double alt2_meters,double distanceKM) {
            var vertical_angle = Math.Atan((alt2_meters - alt1_meters) / (distanceKM*1000));
            var vertical_angle_degrees = ToDegrees(vertical_angle);
            return vertical_angle_degrees;
        }

        static double ToRadians(double deg)
        {
            return deg * Math.PI / 180;
        }
         static double ToDegrees(double rad)
        {
            return rad * 180 / Math.PI;
        }

        /// Text mode map renderer
        class PlaneData {
            public string HexCode { get; set; }
            public string FlightNumber {get; set; }
            public double Latitude  {get; set; }
            public double Longitude {get; set; }
            public double AltitudeFeet {get; set; }
            public double SpeedKnots { get; set; }
            public DateTime LastSeen {get; set; }
            public DateTime? LocationUpdated { get; set; }
            public double DistanceKM {get; set; }
            public double BearingDegrees {get; set; }
            public double ElevationAngleDegrees { get; set; }
        }
        static char[,] PopulateChart(IEnumerable<PlaneData> planes, int chartWidth, int chartHeight, double scale)
        {
            // Cr+eate a chart with the specified dimensions
            char[,] chart = new char[chartWidth, chartHeight];

            // Set all elements of the chart to a space character
            for (int x = 0; x < chartWidth; x++)
            {
                for (int y = 0; y < chartHeight; y++)
                {
                    chart[x, y] = ' ';
                    if ((x==0 || x==chartWidth-1) || (y==0 || y==chartHeight-1))
                    chart[x, y] = '.';

                }
            }

            // Calculate the center of the chart
            int centerX = chartWidth / 2;
            int centerY = chartHeight / 2;

            // Plot the location of each plane on the chart
            foreach (PlaneData plane in planes)
            {
                //dont display planes that have no location data
                if (!plane.LocationUpdated.HasValue) continue;

                //TODO scale plane distance
                double distance = plane.DistanceKM/scale;
                var xScaleFactor = 2;
                var yScaleFactor = 1;

                // Calculate the coordinates of the plane on the chart
                double bearingRadians = ToRadians(plane.BearingDegrees);
                int x = (int)(centerX + distance * xScaleFactor * Math.Sin(bearingRadians));
                int y = (int)(centerY - distance * yScaleFactor * Math.Cos(bearingRadians));

                //Off-chart planes shown at edge
                if (x<0) x=0;
                if (x>chartWidth-1) x = chartWidth-1;
                if (y<0) y=0;
                if (y>chartHeight-1) y = chartHeight-1;

                // Plot the plane on the chart if the coordinates are within the bounds of the chart
                if (x >= 0 && x < chartWidth && y >= 0 && y < chartHeight)
                {
                    chart[x, y] = '*';

                    for (int c=0; c<plane.FlightNumber.Length && x+c+1 < chartWidth; c++)
                    {
                        chart[x + c + 1, y] = plane.FlightNumber[c];
                    }
                }
            }

        chart[centerX, centerY] = '+';

            return chart;
        }

        static void RenderChart(char[,] chart ) {
        
        Console.SetCursorPosition(0,0);
            for (int y = 0; y < chart.GetLength(1); y++)
            {
                for (int x = 0; x < chart.GetLength(0); x++)
                {
                    Console.Write(chart[x, y]);
                }
                Console.WriteLine();
            }       
        }

    }