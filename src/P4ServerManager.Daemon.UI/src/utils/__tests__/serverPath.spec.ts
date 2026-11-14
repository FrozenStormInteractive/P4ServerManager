// Utilities
import { describe, expect, it } from 'vitest'
import { isValidFolderName, joinPath, parentPath, pathName } from '@/utils/serverPath'

describe('parentPath', () => {
  it.each([
    ['/volume1/perforce/main', '/volume1/perforce'],
    ['/volume1/perforce/', '/volume1'],
    ['/volume1', '/'],
    ['/', null],
    ['C:/p4/main', 'C:/p4'],
    ['C:/p4', 'C:/'],
    ['C:/', null],
  ])('gives the parent of %s', (path, parent) => {
    expect(parentPath(path)).toBe(parent)
  })
})

describe('joinPath', () => {
  it.each([
    ['/volume1', 'main', '/volume1/main'],
    ['/', 'srv', '/srv'],
    ['C:/p4', 'main', 'C:/p4/main'],
    ['C:/', 'p4', 'C:/p4'],
  ])('joins %s and %s', (directory, name, path) => {
    expect(joinPath(directory, name)).toBe(path)
  })
})

describe('isValidFolderName', () => {
  it.each(['main', 'p4 root', '.config'])('accepts %s', name => {
    expect(isValidFolderName(name)).toBe(true)
  })

  it.each(['', '.', '..', 'a/b', ' main', 'main '])('rejects "%s"', name => {
    expect(isValidFolderName(name)).toBe(false)
  })
})

describe('pathName', () => {
  it.each([
    ['/volume1/perforce', 'perforce'],
    ['/volume1/perforce/', 'perforce'],
    ['/', '/'],
    ['C:/p4/main', 'main'],
    ['C:/', 'C:/'],
  ])('gives the name of %s', (path, name) => {
    expect(pathName(path)).toBe(name)
  })
})
