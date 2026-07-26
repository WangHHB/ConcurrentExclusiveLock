$ErrorActionPreference = "Stop"

mvn clean package

java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar --help
