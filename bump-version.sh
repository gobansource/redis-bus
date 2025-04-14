#!/bin/bash

# Read the current version
VERSION=$(cat version.txt)

# Split into array
IFS='.' read -ra ADDR <<< "$VERSION"

## Bump the patch version. You can modify this logic as per your versioning strategy.
#ADDR[3]=$((ADDR[3]+1))

# Use the GitHub Actions workflow run number for the last part of the version
ADDR[3]=$GITHUB_RUN_NUMBER

# Construct new version
NEW_VERSION="${ADDR[0]}.${ADDR[1]}.${ADDR[2]}.${ADDR[3]}"

# Update version.txt
echo $NEW_VERSION > version.txt

# Print new version
echo $NEW_VERSION