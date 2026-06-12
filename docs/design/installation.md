# Installation

The repository README.md should have instructions on how to build from source.

The Phaneom.Workspaces GUI looks for a JSON config file in a default location in the user's profile
or in the passed-in location. The config file has:

* The connection to use to access the data repository
  * This could be mongo connection information
  * This could be web access connection information
  * This could be devtunnel -> 
* The name of the devtunnel to use to receive connections, if there is one
* The name of the devtunnel to use to connect to a remote instance, if there is one

The JSON has:

